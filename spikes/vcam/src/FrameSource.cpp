// RaaiCam Phase 0 spike: see FrameSource.h.
#include <windows.h>
#include <sddl.h>
#include <mfapi.h>
#include <mfidl.h>
#include <wil/result.h>
#include <wil/com.h>
#include "Log.h"
#include "FrameSource.h"

static const LONGLONG kLiveWindowMs = 500;
static const int kBlockSize = 64;
static const int kBlockStep = 4;
static const BYTE kPatternY = 0x60;
static const BYTE kPatternUV = 0x80;
static const BYTE kBlockY = 0xEB; // video-range white

FrameSource& FrameSource::Instance()
{
	static FrameSource instance; // lives until DLL unload; keeps the mapping open between streams
	return instance;
}

FrameSource::FrameSource()
{
	LARGE_INTEGER f{};
	QueryPerformanceFrequency(&f);
	_qpcFreq = f.QuadPart ? f.QuadPart : 1;
}

static HANDLE CreateNamed(bool mapping, PCWSTR globalName, PCWSTR localName, PSECURITY_ATTRIBUTES sa, PCWSTR* usedName, bool* existed)
{
	PCWSTR names[] = { globalName, localName };
	for (auto name : names)
	{
		SetLastError(0);
		HANDLE h = mapping
			? CreateFileMappingW(INVALID_HANDLE_VALUE, sa, PAGE_READWRITE, 0, RAAICAM_MAPPING_SIZE, name)
			: CreateEventW(sa, FALSE /*auto-reset*/, FALSE, name);
		auto err = GetLastError();
		if (h)
		{
			VLog(L"%s '%s' OK (%s)", mapping ? L"CreateFileMappingW" : L"CreateEventW", name,
				err == ERROR_ALREADY_EXISTS ? L"opened existing" : L"created new");
			*usedName = name;
			*existed = err == ERROR_ALREADY_EXISTS;
			return h;
		}

		wchar_t what[256];
		swprintf_s(what, L"%s '%s'", mapping ? L"CreateFileMappingW" : L"CreateEventW", name);
		VLogWin32(what, err);
	}
	*usedName = nullptr;
	*existed = false;
	return nullptr;
}

bool FrameSource::EnsureMapping()
{
	if (_header)
		return true;

	// retry at most every 2 s after a failure so RequestSample never stalls
	auto now = GetTickCount64();
	if (_attempted && now - _lastAttemptTick < 2000)
		return false;
	_attempted = true;
	_lastAttemptTick = now;

	wchar_t user[256] = L"?";
	DWORD userLen = _countof(user);
	GetUserNameW(user, &userLen);
	wchar_t image[MAX_PATH] = L"?";
	GetModuleFileNameW(nullptr, image, _countof(image));
	VLog(L"FrameSource: opening shared memory as user '%s' in '%s'", user, image);

	PSECURITY_DESCRIPTOR sd = nullptr;
	if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(RAAICAM_MAPPING_SDDL, SDDL_REVISION_1, &sd, nullptr))
	{
		VLogWin32(L"ConvertStringSecurityDescriptorToSecurityDescriptorW", GetLastError());
		return false;
	}

	SECURITY_ATTRIBUTES sa{ sizeof(sa), sd, FALSE };
	PCWSTR mappingName = nullptr;
	PCWSTR eventName = nullptr;
	bool existed = false;
	bool eventExisted = false;
	_mapping = CreateNamed(true, RAAICAM_MAPPING_NAME_GLOBAL, RAAICAM_MAPPING_NAME_LOCAL, &sa, &mappingName, &existed);
	if (_mapping && !_event)
	{
		_event = CreateNamed(false, RAAICAM_EVENT_NAME_GLOBAL, RAAICAM_EVENT_NAME_LOCAL, &sa, &eventName, &eventExisted);
	}
	LocalFree(sd);

	if (!_mapping)
		return false;

	auto view = MapViewOfFile(_mapping, FILE_MAP_ALL_ACCESS, 0, 0, RAAICAM_MAPPING_SIZE);
	if (!view)
	{
		VLogWin32(L"MapViewOfFile", GetLastError());
		CloseHandle(_mapping);
		_mapping = nullptr;
		return false;
	}

	_header = static_cast<SharedFrameHeader*>(view);
	_frame = static_cast<BYTE*>(view) + RAAICAM_HEADER_SIZE;
	if (!existed || _header->magic != RAAICAM_MAGIC)
	{
		ZeroMemory(_header, RAAICAM_HEADER_SIZE);
		_header->magic = RAAICAM_MAGIC;
		_header->version = RAAICAM_VERSION;
		_header->format = RAAICAM_FORMAT_NV12;
	}

	// don't treat a stale counter from an earlier session as live
	_lastCounter = *reinterpret_cast<volatile uint64_t*>(&_header->frameCounter);
	_lastChangeQpc = 0;
	VLog(L"FrameSource: mapping ready name='%s' event='%s' size=%u counter=%llu", mappingName, eventName ? eventName : L"(none)", RAAICAM_MAPPING_SIZE, _lastCounter);
	return true;
}

void FrameSource::StreamStarted(UINT32 width, UINT32 height)
{
	AcquireSRWLockExclusive(&_lock);
	_activeStreams++;
	if (EnsureMapping())
	{
		_header->magic = RAAICAM_MAGIC;
		_header->version = RAAICAM_VERSION;
		_header->format = RAAICAM_FORMAT_NV12;
		_header->width = width;
		_header->height = height;
		_header->stride = width;
		InterlockedExchange(reinterpret_cast<volatile LONG*>(&_header->consumerActive), 1);
		_lastCounter = *reinterpret_cast<volatile uint64_t*>(&_header->frameCounter);
		_lastChangeQpc = 0;
	}
	_blockX = 0;
	_lastMode = -1;
	VLog(L"FrameSource: stream started %ux%u (active streams=%d)", width, height, _activeStreams);
	ReleaseSRWLockExclusive(&_lock);
}

void FrameSource::StreamStopped()
{
	AcquireSRWLockExclusive(&_lock);
	if (_activeStreams > 0)
		_activeStreams--;
	if (_header && _activeStreams == 0)
	{
		InterlockedExchange(reinterpret_cast<volatile LONG*>(&_header->consumerActive), 0);
	}
	VLog(L"FrameSource: stream stopped (active streams=%d, frames served=%llu)", _activeStreams, _framesServed);
	ReleaseSRWLockExclusive(&_lock);
}

void FrameSource::FillPattern(BYTE* y, LONG pitch, BYTE* uv, UINT32 width, UINT32 height)
{
	for (UINT32 row = 0; row < height; row++)
		memset(y + (size_t)row * pitch, kPatternY, width);
	for (UINT32 row = 0; row < height / 2; row++)
		memset(uv + (size_t)row * pitch, kPatternUV, width);

	if (width <= (UINT32)kBlockSize || height <= (UINT32)kBlockSize)
		return;

	auto range = (int)width - kBlockSize;
	if (_blockX > range)
		_blockX = 0;
	auto top = (int)(height / 2) - kBlockSize / 2;
	for (int row = top; row < top + kBlockSize; row++)
		memset(y + (size_t)row * pitch + _blockX, kBlockY, kBlockSize);

	_blockX += kBlockStep;
	if (_blockX > range)
		_blockX = 0;
}

HRESULT FrameSource::GetFrame(IMFSample* sample, UINT32 width, UINT32 height)
{
	RETURN_HR_IF_NULL(E_POINTER, sample);
	RETURN_HR_IF(E_INVALIDARG, width == 0 || height == 0 || width > RAAICAM_MAX_WIDTH || height > RAAICAM_MAX_HEIGHT);

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
		pitch = (LONG)width;
		length = maxLength;
	}

	auto needed = (DWORD)pitch * height * 3 / 2;
	HRESULT hr = S_OK;
	if (pitch < (LONG)width || length < needed)
	{
		VLog(L"FrameSource: buffer too small pitch=%ld length=%lu needed=%lu", pitch, length, needed);
		hr = E_UNEXPECTED;
	}
	else
	{
		auto y = scan0;
		auto uv = scan0 + (size_t)pitch * height;

		AcquireSRWLockExclusive(&_lock);
		bool live = false;
		if (EnsureMapping())
		{
			LARGE_INTEGER now{};
			QueryPerformanceCounter(&now);
			auto counter = *reinterpret_cast<volatile uint64_t*>(&_header->frameCounter);
			if (counter != _lastCounter)
			{
				_lastCounter = counter;
				_lastChangeQpc = now.QuadPart;
			}

			// Fresh = we saw the counter move within 500 ms AND the writer's own QPC stamp is within
			// 500 ms. The second check stops a counter that advanced while no stream was running
			// (writer since stopped) from showing a stale frame for 500 ms after the next start.
			auto ageMs = _lastChangeQpc ? (now.QuadPart - _lastChangeQpc) * 1000 / _qpcFreq : LLONG_MAX;
			auto writerAgeMs = (now.QuadPart - *reinterpret_cast<volatile int64_t*>(&_header->timestampQpc)) * 1000 / _qpcFreq;
			auto srcStride = _header->stride;
			live = ageMs <= kLiveWindowMs && writerAgeMs >= 0 && writerAgeMs <= kLiveWindowMs &&
				_header->width == width && _header->height == height &&
				_header->format == RAAICAM_FORMAT_NV12 &&
				srcStride >= width && (uint64_t)srcStride * height * 3 / 2 <= RAAICAM_MAX_FRAME_BYTES;

			if (live)
			{
				// single buffer, no lock with the writer: tearing is possible and accepted for the spike
				auto src = _frame;
				for (UINT32 row = 0; row < height; row++)
					memcpy(y + (size_t)row * pitch, src + (size_t)row * srcStride, width);
				src += (size_t)srcStride * height;
				for (UINT32 row = 0; row < height / 2; row++)
					memcpy(uv + (size_t)row * pitch, src + (size_t)row * srcStride, width);
			}
		}

		if (!live)
			FillPattern(y, pitch, uv, width, height);

		int mode = live ? 1 : 0;
		if (mode != _lastMode)
		{
			VLog(L"FrameSource: now serving %s at %ux%u (counter=%llu, frames served=%llu)", live ? L"LIVE writer frames" : L"NO-SIGNAL pattern", width, height, _lastCounter, _framesServed);
			_lastMode = mode;
		}
		_framesServed++;
		ReleaseSRWLockExclusive(&_lock);
	}

	if (locked2d)
		buffer2d->Unlock2D();
	else
		buffer->Unlock();

	if (SUCCEEDED(hr))
		hr = buffer->SetCurrentLength(needed);
	return hr;
}
