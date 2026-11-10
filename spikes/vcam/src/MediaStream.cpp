// Derived from smourier/VCamSample (https://github.com/smourier/VCamSample)
// Copyright (c) 2024-2026 Simon Mourier. MIT License; see spikes/THIRD_PARTY_NOTICES.md.
// Modified for the RaaiCam Phase 0 spike.
#include "pch.h"
#include "Undocumented.h"
#include "Tools.h"
#include "EnumNames.h"
#include "MFTools.h"
#include "FrameSource.h"
#include "MediaStream.h"
#include "MediaSource.h"

HRESULT MediaStream::Initialize(IMFMediaSource* source, int index)
{
	RETURN_HR_IF_NULL(E_POINTER, source);
	_source = source;
	_index = index;

	RETURN_IF_FAILED(SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
	RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_STREAM_ID, index));
	RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
	RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes::MFFrameSourceTypes_Color));

	RETURN_IF_FAILED(MFCreateEventQueue(&_queue));

	// RaaiCam: NV12 only, 1920x1080@30 and 1280x720@30 (Global Constraints)
	static const UINT32 sizes[][2] = { { 1920, 1080 }, { 1280, 720 } };
	auto types = wil::make_unique_cotaskmem_array<wil::com_ptr_nothrow<IMFMediaType>>(_countof(sizes));
	for (size_t i = 0; i < _countof(sizes); i++)
	{
		auto w = sizes[i][0];
		auto h = sizes[i][1];
		wil::com_ptr_nothrow<IMFMediaType> nv12Type;
		RETURN_IF_FAILED(MFCreateMediaType(&nv12Type));
		nv12Type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
		nv12Type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
		nv12Type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
		nv12Type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
		MFSetAttributeSize(nv12Type.get(), MF_MT_FRAME_SIZE, w, h);
		nv12Type->SetUINT32(MF_MT_DEFAULT_STRIDE, w); // Y-plane stride
		nv12Type->SetUINT32(MF_MT_SAMPLE_SIZE, w * h * 3 / 2);
		nv12Type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE);
		MFSetAttributeRatio(nv12Type.get(), MF_MT_FRAME_RATE, 30, 1);
		// frame size * pixel bit size * framerate
		nv12Type->SetUINT32(MF_MT_AVG_BITRATE, (UINT32)(w * h * 12ull * 30));
		MFSetAttributeRatio(nv12Type.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
		types[i] = nv12Type.detach();
	}

	RETURN_IF_FAILED_MSG(MFCreateStreamDescriptor(_index, (DWORD)types.size(), types.get(), &_descriptor), "MFCreateStreamDescriptor failed");

	wil::com_ptr_nothrow<IMFMediaTypeHandler> handler;
	RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(&handler));
	TraceMFAttributes(handler.get(), L"MediaTypeHandler");
	RETURN_IF_FAILED(handler->SetCurrentMediaType(types[0]));

	return S_OK;
}

HRESULT MediaStream::Start(IMFMediaType* type)
{
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_allocator);

	wil::com_ptr_nothrow<IMFMediaType> current;
	if (!type)
	{
		wil::com_ptr_nothrow<IMFMediaTypeHandler> handler;
		RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(&handler));
		RETURN_IF_FAILED(handler->GetCurrentMediaType(&current));
		type = current.get();
	}

	RETURN_IF_FAILED(type->GetGUID(MF_MT_SUBTYPE, &_format));
	RETURN_IF_FAILED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &_width, &_height));
	VLog(L"MediaStream::Start %ux%u subtype=%s", _width, _height, GUID_ToStringW(_format).c_str());
	RETURN_HR_IF_MSG(MF_E_INVALIDMEDIATYPE, _format != MFVideoFormat_NV12, "Only NV12 is supported");

	RETURN_IF_FAILED(_allocator->InitializeSampleAllocator(10, type));
	FrameSource::Instance().StreamStarted(_width, _height);
	RETURN_IF_FAILED(_queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr));
	_state = MF_STREAM_STATE_RUNNING;
	return S_OK;
}

HRESULT MediaStream::Stop()
{
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_allocator);

	VLog(L"MediaStream::Stop (state was %u)", _state);
	if (_state != MF_STREAM_STATE_STOPPED)
	{
		FrameSource::Instance().StreamStopped();
	}
	RETURN_IF_FAILED(_allocator->UninitializeSampleAllocator());
	RETURN_IF_FAILED(_queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr));
	_state = MF_STREAM_STATE_STOPPED;
	return S_OK;
}

MFSampleAllocatorUsage MediaStream::GetAllocatorUsage()
{
	return MFSampleAllocatorUsage_UsesProvidedAllocator;
}

HRESULT MediaStream::SetAllocator(IUnknown* allocator)
{
	RETURN_HR_IF_NULL(E_POINTER, allocator);
	_allocator.reset();
	RETURN_HR(allocator->QueryInterface(&_allocator));
}

HRESULT MediaStream::SetD3DManager(IUnknown* manager)
{
	RETURN_HR_IF_NULL(E_POINTER, manager);

	// RaaiCam: CPU path only. Not handing the DXGI manager to the allocator keeps samples in
	// system memory, so FrameSource can memcpy from shared memory directly.
	VLog(L"MediaStream::SetD3DManager ignored (CPU samples)");
	return S_OK;
}

void MediaStream::Shutdown()
{
	if (_queue)
	{
		LOG_IF_FAILED_MSG(_queue->Shutdown(), "Queue shutdown failed");
		_queue.reset();
	}

	_descriptor.reset();
	_source.reset();
	_attributes.reset();
}

// IMFMediaEventGenerator
STDMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState)
{
	//WINTRACE(L"MediaSource::BeginGetEvent");
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->BeginGetEvent(pCallback, punkState));
	return S_OK;
}

STDMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent)
{
	//WINTRACE(L"MediaStream::EndGetEvent");
	RETURN_HR_IF_NULL(E_POINTER, ppEvent);
	*ppEvent = nullptr;
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->EndGetEvent(pResult, ppEvent));
	return S_OK;
}

STDMETHODIMP MediaStream::GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent)
{
	WINTRACE(L"MediaStream::GetEvent");
	RETURN_HR_IF_NULL(E_POINTER, ppEvent);
	*ppEvent = nullptr;
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->GetEvent(dwFlags, ppEvent));
	return S_OK;
}

STDMETHODIMP MediaStream::QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue)
{
	WINTRACE(L"MediaStream::QueueEvent");
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue));
	return S_OK;
}

// IMFMediaStream
STDMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** ppMediaSource)
{
	WINTRACE(L"MediaSource::GetMediaSource");
	RETURN_HR_IF_NULL(E_POINTER, ppMediaSource);
	*ppMediaSource = nullptr;
	RETURN_HR_IF(MF_E_SHUTDOWN, !_source);

	RETURN_IF_FAILED(_source.copy_to(ppMediaSource));
	return S_OK;
}

STDMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor)
{
	WINTRACE(L"MediaStream::GetStreamDescriptor");
	RETURN_HR_IF_NULL(E_POINTER, ppStreamDescriptor);
	*ppStreamDescriptor = nullptr;
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_descriptor);

	RETURN_IF_FAILED(_descriptor.copy_to(ppStreamDescriptor));
	return S_OK;
}

STDMETHODIMP MediaStream::RequestSample(IUnknown* pToken)
{
	//WINTRACE(L"MediaStream::RequestSample pToken:%p", pToken);
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_allocator || !_queue);

	wil::com_ptr_nothrow<IMFSample> sample;
	RETURN_IF_FAILED(_allocator->AllocateSample(&sample));
	RETURN_IF_FAILED(sample->SetSampleTime(MFGetSystemTime()));
	RETURN_IF_FAILED(sample->SetSampleDuration(333333));

	// RaaiCam: pace delivery at 30 fps. Upstream delivers on every request, which Frame Server
	// issues back to back (measured ~2500 samples/s), so the "4 px/frame" block and CPU use are
	// meaningless without it. Sleeps at most one frame period.
	{
		LARGE_INTEGER now{}, freq{};
		QueryPerformanceCounter(&now);
		QueryPerformanceFrequency(&freq);
		auto period = freq.QuadPart / 30;
		if (_nextSampleQpc > now.QuadPart)
		{
			auto waitMs = (DWORD)((_nextSampleQpc - now.QuadPart) * 1000 / freq.QuadPart);
			if (waitMs > 0)
				Sleep(waitMs < 34 ? waitMs : 34);
			_nextSampleQpc += period;
		}
		else
		{
			_nextSampleQpc = now.QuadPart + period;
		}
	}

	// RaaiCam: fill from shared memory or the "no signal" pattern
	RETURN_IF_FAILED(FrameSource::Instance().GetFrame(sample.get(), _width, _height));
	auto& outSample = sample;

	if (pToken)
	{
		RETURN_IF_FAILED(outSample->SetUnknown(MFSampleExtension_Token, pToken));
	}
	RETURN_IF_FAILED(_queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, outSample.get()));
	return S_OK;
}

// IMFMediaStream2
STDMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE value)
{
	WINTRACE(L"MediaStream::SetStreamState current:%u value:%u", _state, value);
	VLog(L"MediaStream::SetStreamState current:%u value:%u", _state, value);
	if (_state == value) // RaaiCam: upstream had "=" (assignment)
		return S_OK;
	switch (value)
	{
	case MF_STREAM_STATE_PAUSED:
		if (_state != MF_STREAM_STATE_RUNNING)
			RETURN_HR(MF_E_INVALID_STATE_TRANSITION);

		_state = value;
		break;

	case MF_STREAM_STATE_RUNNING:
		if (_state == MF_STREAM_STATE_PAUSED) { _state = value; break; } // RaaiCam: resume without re-initializing the allocator
		RETURN_IF_FAILED(Start(nullptr));
		break;

	case MF_STREAM_STATE_STOPPED:
		RETURN_IF_FAILED(Stop());
		break;

	default:
		RETURN_HR(MF_E_INVALID_STATE_TRANSITION);
		break;
	}
	return S_OK;
}

STDMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* value)
{
	WINTRACE(L"MediaStream::GetStreamState state:%u", _state);
	RETURN_HR_IF_NULL(E_POINTER, value);
	*value = _state;
	return S_OK;
}

// IKsControl
STDMETHODIMP_(NTSTATUS) MediaStream::KsProperty(PKSPROPERTY property, ULONG length, LPVOID data, ULONG dataLength, ULONG* bytesReturned)
{
	WINTRACE(L"MediaStream::KsProperty len:%u data:%p dataLength:%u", length, data, dataLength);
	RETURN_HR_IF_NULL(E_POINTER, property);
	RETURN_HR_IF_NULL(E_POINTER, bytesReturned);
	winrt::slim_lock_guard lock(_lock);

	WINTRACE(L"MediaStream::KsProperty prop:%s", PKSIDENTIFIER_ToString(property, length).c_str());

	return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP_(NTSTATUS) MediaStream::KsMethod(PKSMETHOD method, ULONG length, LPVOID data, ULONG dataLength, ULONG* bytesReturned)
{
	WINTRACE(L"MediaStream::KsMethod len:%u data:%p dataLength:%u", length, data, dataLength);
	RETURN_HR_IF_NULL(E_POINTER, method);
	RETURN_HR_IF_NULL(E_POINTER, bytesReturned);
	winrt::slim_lock_guard lock(_lock);

	WINTRACE(L"MediaStream::KsMethod method:%s", PKSIDENTIFIER_ToString(method, length).c_str());

	return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP_(NTSTATUS) MediaStream::KsEvent(PKSEVENT evt, ULONG length, LPVOID data, ULONG dataLength, ULONG* bytesReturned)
{
	WINTRACE(L"MediaStream::KsEvent evt:%p len:%u data:%p dataLength:%u", evt, length, data, dataLength);
	RETURN_HR_IF_NULL(E_POINTER, bytesReturned);
	winrt::slim_lock_guard lock(_lock);

	WINTRACE(L"MediaStream::KsEvent event:%s", PKSIDENTIFIER_ToString(evt, length).c_str());
	return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}
