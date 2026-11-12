// Derived from smourier/VCamSample (https://github.com/smourier/VCamSample)
// Copyright (c) 2024-2026 Simon Mourier. MIT License; see windows/THIRD_PARTY_NOTICES.md.
// Modified for RaaiCam: timer-paced delivery (DeliveryClock), per-stream frame state, lock discipline.
// Lock order and threading rules: see the top of MediaSource.cpp.
#include "pch.h"
#include "Undocumented.h"
#include "Tools.h"
#include "EnumNames.h"
#include "MFTools.h"
#include "PlaceholderCard.h"
#include "SharedFrameV2.h"
#include "SharedRegion.h"
#include "MediaStream.h"
#include "MediaSource.h"

namespace
{
	constexpr size_t kMaxPendingRequests = 64;
	constexpr LONGLONG kSampleDuration = 333333; // 100 ns units, 30 fps

	bool IsSupportedSize(UINT32 w, UINT32 h)
	{
		return (w == 1920 && h == 1080) || (w == 1280 && h == 720);
	}

	// QPC ticks -> 100 ns units without overflowing int64 (QPC * 10^7 overflows after ~25 h at 10 MHz).
	LONGLONG QpcTo100ns(int64_t qpc, int64_t freq)
	{
		return (qpc / freq) * 10'000'000 + ((qpc % freq) * 10'000'000) / freq;
	}
}

HRESULT MediaStream::Initialize(IMFMediaSource* source, int index)
{
	RETURN_HR_IF_NULL(E_POINTER, source);
	_source = source;
	_index = index;

	LARGE_INTEGER f{};
	QueryPerformanceFrequency(&f);
	_qpcFreq = f.QuadPart > 0 ? f.QuadPart : 1;

	RETURN_IF_FAILED(SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
	RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_STREAM_ID, index));
	RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
	RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes::MFFrameSourceTypes_Color));

	RETURN_IF_FAILED(MFCreateEventQueue(&_queue));

	// RaaiCam: NV12 only, exactly 1920x1080@30 and 1280x720@30 (Global Constraints)
	static const UINT32 sizes[][2] = { { 1920, 1080 }, { 1280, 720 } };
	auto types = wil::make_unique_cotaskmem_array<wil::com_ptr_nothrow<IMFMediaType>>(_countof(sizes));
	RETURN_IF_NULL_ALLOC(types.get());
	for (size_t i = 0; i < _countof(sizes); i++)
	{
		auto w = sizes[i][0];
		auto h = sizes[i][1];
		wil::com_ptr_nothrow<IMFMediaType> nv12Type;
		RETURN_IF_FAILED(MFCreateMediaType(&nv12Type));
		RETURN_IF_FAILED(nv12Type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
		RETURN_IF_FAILED(nv12Type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
		RETURN_IF_FAILED(nv12Type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
		RETURN_IF_FAILED(nv12Type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
		RETURN_IF_FAILED(MFSetAttributeSize(nv12Type.get(), MF_MT_FRAME_SIZE, w, h));
		RETURN_IF_FAILED(nv12Type->SetUINT32(MF_MT_DEFAULT_STRIDE, w)); // Y-plane stride
		RETURN_IF_FAILED(nv12Type->SetUINT32(MF_MT_SAMPLE_SIZE, w * h * 3 / 2));
		RETURN_IF_FAILED(nv12Type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE));
		RETURN_IF_FAILED(MFSetAttributeRatio(nv12Type.get(), MF_MT_FRAME_RATE, 30, 1));
		// frame size * pixel bit size * framerate
		RETURN_IF_FAILED(nv12Type->SetUINT32(MF_MT_AVG_BITRATE, (UINT32)(w * h * 12ull * 30)));
		RETURN_IF_FAILED(MFSetAttributeRatio(nv12Type.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
		types[i] = nv12Type.detach();
	}

	RETURN_IF_FAILED_MSG(MFCreateStreamDescriptor(_index, (DWORD)types.size(), types.get(), &_descriptor), "MFCreateStreamDescriptor failed");

	wil::com_ptr_nothrow<IMFMediaTypeHandler> handler;
	RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(&handler));
	TraceMFAttributes(handler.get(), L"MediaTypeHandler");
	RETURN_IF_FAILED(handler->SetCurrentMediaType(types[0]));

	return S_OK;
}

// Caller holds _lock.
void MediaStream::DetachClockLocked(Teardown& t)
{
	t.clock = std::move(_clock);
	t.clockRef = std::move(_clockRef);
}

// Called WITHOUT the stream lock (the caller may hold the source lock: the tick never takes it).
void MediaStream::FinishTeardown(Teardown& t)
{
	if (t.clock)
	{
		t.clock->Stop(); // cancel + WaitForThreadpoolTimerCallbacks(TRUE) + close
		t.clock.reset();
	}
	t.clockRef = nullptr; // no callback can run any more: drop the timer's keep-alive reference
}

HRESULT MediaStream::Start(IMFMediaType* type)
{
	winrt::slim_lock_guard lock(_lock); // RaaiCam: serialize with RequestSample/Stop/Shutdown/tick
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_allocator || !_descriptor);

	wil::com_ptr_nothrow<IMFMediaType> current;
	if (!type)
	{
		wil::com_ptr_nothrow<IMFMediaTypeHandler> handler;
		RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(&handler));
		RETURN_IF_FAILED(handler->GetCurrentMediaType(&current));
		type = current.get();
	}

	GUID format = GUID_NULL;
	UINT32 width = 0, height = 0;
	RETURN_IF_FAILED(type->GetGUID(MF_MT_SUBTYPE, &format));
	RETURN_IF_FAILED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &width, &height));
	if (format != MFVideoFormat_NV12 || !IsSupportedSize(width, height))
	{
		VLog(L"MediaStream[%d]::Start rejected %ux%u (only NV12 1920x1080 / 1280x720)", _index, width, height);
		return MF_E_INVALIDMEDIATYPE;
	}

	if (_state != MF_STREAM_STATE_STOPPED)
	{
		// Already started (clock running): a restart of a live stream only re-signals.
		RETURN_HR_IF(MF_E_INVALID_STATE_TRANSITION, width != _width || height != _height);
		RETURN_IF_FAILED(_queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr));
		_state = MF_STREAM_STATE_RUNNING;
		return S_OK;
	}

	std::unique_ptr<raaicam::DeliveryClock> clock;
	try
	{
		_frame.assign(size_t(width) * height * 3 / 2, uint8_t(0));
		_reader = raaicam::FrameReader{};
		clock = std::make_unique<raaicam::DeliveryClock>();
	}
	catch (...)
	{
		return E_OUTOFMEMORY;
	}

	RETURN_IF_FAILED(_allocator->InitializeSampleAllocator(10, type));
	// Unarmed timer: if anything below fails, destroying it here cannot wait on a callback.
	HRESULT hr = clock->Create(&MediaStream::TickThunk, this, g_module);
	if (SUCCEEDED(hr))
		hr = _queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
	if (FAILED(hr))
	{
		LOG_IF_FAILED(_allocator->UninitializeSampleAllocator());
		VLog(L"MediaStream[%d]::Start failed hr=0x%08X", _index, hr);
		return hr;
	}

	_format = format;
	_width = width;
	_height = height;
	_cardTick = 0;
	_lastMode = -1;
	_framesDelivered = 0;
	_pending.clear();
	_loggedPendingFull = false;
	_allocEmpty = false;
	_allocEmptySinceQpc = 0;
	_clockRef = get_strong(); // keeps this stream alive until FinishTeardown, after the last callback
	_clock = std::move(clock);
	_state = MF_STREAM_STATE_RUNNING;
	raaicam::SharedRegion::Instance().OnStreamStarted(this, width, height); // leaf lock, no I/O
	_clock->Arm();
	VLog(L"MediaStream[%d] started %ux%u NV12 @30", _index, width, height);
	return S_OK;
}

HRESULT MediaStream::Stop()
{
	Teardown teardown;
	HRESULT hr = S_OK;
	{
		winrt::slim_lock_guard lock(_lock); // RaaiCam: serialize with RequestSample/Start/Shutdown/tick
		RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_allocator);

		DetachClockLocked(teardown);
		_pending.clear(); // outstanding requests are discarded on stop
		if (_state != MF_STREAM_STATE_STOPPED)
		{
			_state = MF_STREAM_STATE_STOPPED;
			raaicam::SharedRegion::Instance().OnStreamStopped(this);
			VLog(L"MediaStream[%d] stopped (%llu samples delivered)", _index, _framesDelivered);
		}
		_allocEmpty = false;
		// The stop itself has happened: failures below are logged, not returned.
		LOG_IF_FAILED(_allocator->UninitializeSampleAllocator());
		LOG_IF_FAILED(_queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr));
	}
	// Outside the stream lock: cancel the timer and wait for a running tick (which takes the stream lock).
	FinishTeardown(teardown);
	return hr;
}

MFSampleAllocatorUsage MediaStream::GetAllocatorUsage()
{
	return MFSampleAllocatorUsage_UsesProvidedAllocator;
}

HRESULT MediaStream::SetAllocator(IUnknown* allocator)
{
	RETURN_HR_IF_NULL(E_POINTER, allocator);
	wil::com_ptr_nothrow<IMFVideoSampleAllocatorEx> next;
	RETURN_IF_FAILED(allocator->QueryInterface(IID_PPV_ARGS(&next)));
	wil::com_ptr_nothrow<IMFVideoSampleAllocatorEx> previous; // released after the lock
	{
		winrt::slim_lock_guard lock(_lock);
		previous = std::move(_allocator);
		_allocator = std::move(next);
	}
	return S_OK;
}

HRESULT MediaStream::SetD3DManager(IUnknown* manager)
{
	RETURN_HR_IF_NULL(E_POINTER, manager);

	// RaaiCam: CPU path only. Not handing the DXGI manager to the allocator keeps samples in
	// system memory, so frames can be copied from shared memory directly.
	return S_OK;
}

void MediaStream::Shutdown()
{
	Teardown teardown;
	wil::com_ptr_nothrow<IMFMediaSource> source;            // released after the lock (never Release the
	wil::com_ptr_nothrow<IMFVideoSampleAllocatorEx> allocator; // source while holding the stream lock)
	{
		winrt::slim_lock_guard lock(_lock); // RaaiCam: upstream reset these without the lock
		DetachClockLocked(teardown);
		_pending.clear();
		if (_state != MF_STREAM_STATE_STOPPED)
		{
			raaicam::SharedRegion::Instance().OnStreamStopped(this); // shutdown without Stop: keep consumerActive accurate
			_state = MF_STREAM_STATE_STOPPED;
			VLog(L"MediaStream[%d] shut down while running (%llu samples delivered)", _index, _framesDelivered);
		}
		if (_queue)
		{
			LOG_IF_FAILED_MSG(_queue->Shutdown(), "Queue shutdown failed");
			_queue.reset();
		}
		_descriptor.reset();
		source = std::move(_source);
		allocator = std::move(_allocator);
		// RaaiCam: _attributes is kept (upstream reset it): CBaseAttributes dereferences it unchecked,
		// so a late GetItem after shutdown would crash Frame Server.
	}
	FinishTeardown(teardown);
}

void MediaStream::TickThunk(void* context, int64_t tickQpc)
{
	// The stream is kept alive by _clockRef until FinishTeardown, which runs only after
	// WaitForThreadpoolTimerCallbacks returned, so `context` is always a live stream here.
	static_cast<MediaStream*>(context)->OnTick(tickQpc);
}

// Caller holds _lock.
bool MediaStream::ShouldLogErrorLocked(HRESULT hr)
{
	LARGE_INTEGER now{};
	QueryPerformanceCounter(&now);
	for (auto& e : _errLog)
	{
		if (e.hr == hr)
		{
			if (now.QuadPart - e.qpc < 5 * _qpcFreq)
				return false;
			e.qpc = now.QuadPart;
			return true;
		}
	}
	_errLog[_errLogNext] = { hr, now.QuadPart };
	_errLogNext = (_errLogNext + 1) % _countof(_errLog);
	return true;
}

// Threadpool callback (one at a time per stream). Never blocks: the stream lock is only held by
// non-blocking code elsewhere. Never takes the source lock.
void MediaStream::OnTick(int64_t tickQpc)
{
	// Outside the stream lock: at most one mapping attempt (registry + CreateFileMapping) per 2 s.
	uint8_t* region = raaicam::SharedRegion::Instance().TryAcquire();

	// Declared before the lock guard: released only after the stream lock is dropped.
	wil::com_ptr_nothrow<IUnknown> token;
	wil::com_ptr_nothrow<IMFSample> sample;

	winrt::slim_lock_guard lock(_lock);
	if (_state != MF_STREAM_STATE_RUNNING || !_queue || !_allocator)
		return;
	raaicam::SharedRegion::Instance().Heartbeat(tickQpc);
	if (_pending.empty())
		return;

	// Peek, don't pop: on MF_E_SAMPLEALLOCATOR_EMPTY the request stays at the FRONT of _pending
	// (same effect as pushing it back, but cannot fail) and is retried on the next tick.
	HRESULT hr = _allocator->AllocateSample(&sample);
	if (hr == MF_E_SAMPLEALLOCATOR_EMPTY)
	{
		// The consumer holds every sample: transient back-pressure, not a stream error. Only >= 1 s of
		// continuous EMPTY raises MEError.
		if (!_allocEmpty)
		{
			_allocEmpty = true;
			_allocEmptySinceQpc = tickQpc;
			VLog(L"MediaStream[%d]: sample allocator empty, holding %zu request(s) and retrying each tick", _index, _pending.size());
			return;
		}
		if (tickQpc - _allocEmptySinceQpc < _qpcFreq)
			return;
		VLog(L"MediaStream[%d]: sample allocator empty for >= 1 s, queueing MEError", _index);
		token = std::move(_pending.front());
		_pending.pop_front();
		_allocEmpty = false;
		LOG_IF_FAILED(_queue->QueueEventParamVar(MEError, GUID_NULL, hr, nullptr));
		return;
	}
	if (_allocEmpty && SUCCEEDED(hr))
	{
		_allocEmpty = false;
		VLog(L"MediaStream[%d]: sample allocator available again", _index);
	}

	token = std::move(_pending.front());
	_pending.pop_front();
	if (FAILED(hr))
	{
		// Any other allocation failure: report it, drop this request.
		_allocEmpty = false;
		if (ShouldLogErrorLocked(hr))
			VLog(L"MediaStream[%d]: AllocateSample failed hr=0x%08X, queueing MEError", _index, hr);
		LOG_IF_FAILED(_queue->QueueEventParamVar(MEError, GUID_NULL, hr, nullptr));
		return;
	}

	hr = FillSampleLocked(sample.get(), tickQpc, region);
	if (SUCCEEDED(hr))
		hr = sample->SetSampleTime(QpcTo100ns(tickQpc, _qpcFreq));
	if (SUCCEEDED(hr))
		hr = sample->SetSampleDuration(kSampleDuration);
	if (SUCCEEDED(hr) && token)
		hr = sample->SetUnknown(MFSampleExtension_Token, token.get());
	if (SUCCEEDED(hr))
		hr = _queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.get());
	if (FAILED(hr))
	{
		if (ShouldLogErrorLocked(hr))
			VLog(L"MediaStream[%d]: producing a sample failed hr=0x%08X, queueing MEError", _index, hr);
		LOG_IF_FAILED(_queue->QueueEventParamVar(MEError, GUID_NULL, hr, nullptr));
		return;
	}
	_framesDelivered++;
}

// Caller holds _lock. Live frame (Fresh), last good frame (Reused, <= 500 ms) or the placeholder card.
HRESULT MediaStream::FillSampleLocked(IMFSample* sample, int64_t tickQpc, uint8_t* region)
{
	wil::com_ptr_nothrow<IMFMediaBuffer> buffer;
	RETURN_IF_FAILED(sample->GetBufferByIndex(0, &buffer));

	BYTE* scan0 = nullptr;
	LONG pitch = 0;
	BYTE* start = nullptr;
	DWORD length = 0;
	bool locked2d = false;
	auto buffer2d = buffer.try_query<IMF2DBuffer2>();
	if (buffer2d && SUCCEEDED(buffer2d->Lock2DSize(MF2DBuffer_LockFlags_Write, &scan0, &pitch, &start, &length)))
	{
		locked2d = true;
	}
	else
	{
		DWORD maxLength = 0;
		RETURN_IF_FAILED(buffer->Lock(&scan0, &maxLength, nullptr));
		pitch = (LONG)_width;
		length = maxLength;
	}

	const UINT32 w = _width;
	const UINT32 h = _height;
	HRESULT hr = S_OK;
	const uint64_t needed = pitch > 0 ? uint64_t(pitch) * h * 3 / 2 : 0;
	if (pitch < (LONG)w || needed > length)
	{
		hr = MF_E_BUFFERTOOSMALL;
	}
	else
	{
		auto result = raaicam::ReadResult::Stale;
		if (region && !_frame.empty())
		{
			try
			{
				result = _reader.Read(region, raaicam::kMappingBytes, w, h, tickQpc, _qpcFreq, _frame.data(), _frame.size(), w);
			}
			catch (...)
			{
				result = raaicam::ReadResult::Stale; // e.g. staging allocation failed: serve the card
			}
		}

		if (result == raaicam::ReadResult::Stale)
		{
			raaicam::DrawPlaceholderCard(scan0, (uint32_t)pitch, w, h, _cardTick++);
		}
		else
		{
			// Fresh: _frame was just refreshed; Reused: _frame still holds the last good frame.
			const uint8_t* srcY = _frame.data();
			const uint8_t* srcUV = srcY + size_t(w) * h;
			uint8_t* dstUV = scan0 + size_t(pitch) * h;
			for (UINT32 row = 0; row < h; row++)
				memcpy(scan0 + size_t(row) * pitch, srcY + size_t(row) * w, w);
			for (UINT32 row = 0; row < h / 2; row++)
				memcpy(dstUV + size_t(row) * pitch, srcUV + size_t(row) * w, w);
		}

		const int mode = result == raaicam::ReadResult::Stale ? 0 : 1;
		if (mode != _lastMode)
		{
			VLog(L"MediaStream[%d]: now serving %s at %ux%u (%llu samples delivered)", _index,
				mode ? L"live frames" : (region ? L"the placeholder card (no fresh writer frame)" : L"the placeholder card (no mapping)"),
				w, h, _framesDelivered);
			_lastMode = mode;
		}
	}

	if (locked2d)
		buffer2d->Unlock2D();
	else
		buffer->Unlock();

	if (SUCCEEDED(hr))
		hr = buffer->SetCurrentLength((DWORD)needed);
	return hr;
}

// IMFMediaEventGenerator
STDMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState)
{
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->BeginGetEvent(pCallback, punkState));
	return S_OK;
}

STDMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent)
{
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

	// RaaiCam: GetEvent may block until an event arrives. Never block holding the stream lock
	// (the delivery tick needs it to queue the next event): take a reference and wait outside it.
	wil::com_ptr_nothrow<IMFMediaEventQueue> queue;
	{
		winrt::slim_lock_guard lock(_lock);
		RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
		queue = _queue;
	}
	RETURN_IF_FAILED(queue->GetEvent(dwFlags, ppEvent));
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
	WINTRACE(L"MediaStream::GetMediaSource");
	RETURN_HR_IF_NULL(E_POINTER, ppMediaSource);
	*ppMediaSource = nullptr;
	winrt::slim_lock_guard lock(_lock); // AddRef only; never calls into the source
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
	// RaaiCam: no work and no sleeping here. The request (token) is queued under the stream lock and
	// fulfilled by the DeliveryClock tick, at most one per 33.333 ms.
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_allocator || !_queue);
	if (_state == MF_STREAM_STATE_STOPPED)
		return MF_E_MEDIA_SOURCE_WRONGSTATE; // explicit, not a silent drop

	if (_pending.size() >= kMaxPendingRequests)
	{
		if (!_loggedPendingFull)
		{
			VLog(L"MediaStream[%d]: %zu requests already pending, refusing more", _index, _pending.size());
			_loggedPendingFull = true;
		}
		return MF_E_NOTACCEPTING;
	}

	try
	{
		_pending.emplace_back(pToken);
	}
	catch (...)
	{
		return E_OUTOFMEMORY;
	}
	return S_OK;
}

// IMFMediaStream2
STDMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE value)
{
	WINTRACE(L"MediaStream::SetStreamState value:%u", value);
	switch (value)
	{
	case MF_STREAM_STATE_PAUSED:
	{
		winrt::slim_lock_guard lock(_lock);
		if (_state == value)
			return S_OK;
		if (_state != MF_STREAM_STATE_RUNNING)
			RETURN_HR(MF_E_INVALID_STATE_TRANSITION);

		_state = value; // the clock keeps running; ticks deliver nothing while paused
		VLog(L"MediaStream[%d] paused", _index);
		return S_OK;
	}

	case MF_STREAM_STATE_RUNNING:
	{
		{
			// RaaiCam: resume without re-initializing the allocator
			winrt::slim_lock_guard lock(_lock);
			if (_state == value)
				return S_OK;
			if (_state == MF_STREAM_STATE_PAUSED)
			{
				_state = value;
				VLog(L"MediaStream[%d] resumed", _index);
				return S_OK;
			}
		}
		RETURN_IF_FAILED(Start(nullptr)); // takes _lock itself
		return S_OK;
	}

	case MF_STREAM_STATE_STOPPED:
	{
		{
			winrt::slim_lock_guard lock(_lock);
			if (_state == value)
				return S_OK;
		}
		RETURN_IF_FAILED(Stop()); // takes _lock itself, stops the clock outside it
		return S_OK;
	}

	default:
		RETURN_HR(MF_E_INVALID_STATE_TRANSITION);
	}
}

STDMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* value)
{
	RETURN_HR_IF_NULL(E_POINTER, value);
	winrt::slim_lock_guard lock(_lock);
	*value = _state;
	return S_OK;
}

// IKsControl
STDMETHODIMP_(NTSTATUS) MediaStream::KsProperty(PKSPROPERTY property, ULONG length, LPVOID data, ULONG dataLength, ULONG* bytesReturned)
{
	WINTRACE(L"MediaStream::KsProperty len:%u data:%p dataLength:%u", length, data, dataLength);
	RETURN_HR_IF_NULL(E_POINTER, property);
	RETURN_HR_IF_NULL(E_POINTER, bytesReturned);
	UNREFERENCED_PARAMETER(length);
	UNREFERENCED_PARAMETER(data);
	UNREFERENCED_PARAMETER(dataLength);
	return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP_(NTSTATUS) MediaStream::KsMethod(PKSMETHOD method, ULONG length, LPVOID data, ULONG dataLength, ULONG* bytesReturned)
{
	WINTRACE(L"MediaStream::KsMethod len:%u data:%p dataLength:%u", length, data, dataLength);
	RETURN_HR_IF_NULL(E_POINTER, method);
	RETURN_HR_IF_NULL(E_POINTER, bytesReturned);
	UNREFERENCED_PARAMETER(length);
	UNREFERENCED_PARAMETER(data);
	UNREFERENCED_PARAMETER(dataLength);
	return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP_(NTSTATUS) MediaStream::KsEvent(PKSEVENT evt, ULONG length, LPVOID data, ULONG dataLength, ULONG* bytesReturned)
{
	WINTRACE(L"MediaStream::KsEvent evt:%p len:%u data:%p dataLength:%u", evt, length, data, dataLength);
	RETURN_HR_IF_NULL(E_POINTER, bytesReturned);
	UNREFERENCED_PARAMETER(evt);
	UNREFERENCED_PARAMETER(length);
	UNREFERENCED_PARAMETER(data);
	UNREFERENCED_PARAMETER(dataLength);
	return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}
