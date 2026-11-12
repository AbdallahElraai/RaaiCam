// Derived from smourier/VCamSample (https://github.com/smourier/VCamSample)
// Copyright (c) 2024-2026 Simon Mourier. MIT License; see windows/THIRD_PARTY_NOTICES.md.
// Modified for RaaiCam: timer-paced delivery (DeliveryClock), per-stream frame state, lock discipline.
// Lock order and threading rules: see the top of MediaSource.cpp.
#pragma once

#include <deque>
#include <memory>
#include <vector>

#include "DeliveryClock.h"
#include "FrameReader.h"

struct MediaStream : winrt::implements<MediaStream, CBaseAttributes<IMFAttributes>, IMFMediaStream2, IKsControl>
{
public:
	// IMFMediaEventGenerator
	STDMETHOD(BeginGetEvent)(IMFAsyncCallback* pCallback, IUnknown* punkState);
	STDMETHOD(EndGetEvent)(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent);
	STDMETHOD(GetEvent)(DWORD dwFlags, IMFMediaEvent** ppEvent);
	STDMETHOD(QueueEvent)(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue);

	// IMFMediaStream
	STDMETHOD(GetMediaSource)(IMFMediaSource** ppMediaSource);
	STDMETHOD(GetStreamDescriptor)(IMFStreamDescriptor** ppStreamDescriptor);
	STDMETHOD(RequestSample)(IUnknown* pToken);

	// IMFMediaStream2
	STDMETHOD(SetStreamState)(MF_STREAM_STATE value);
	STDMETHOD(GetStreamState)(MF_STREAM_STATE* value);

	// IKsControl
	STDMETHOD_(NTSTATUS, KsProperty)(PKSPROPERTY Property, ULONG PropertyLength, LPVOID PropertyData, ULONG DataLength, ULONG* BytesReturned);
	STDMETHOD_(NTSTATUS, KsMethod)(PKSMETHOD Method, ULONG MethodLength, LPVOID MethodData, ULONG DataLength, ULONG* BytesReturned);
	STDMETHOD_(NTSTATUS, KsEvent)(PKSEVENT Event, ULONG EventLength, LPVOID EventData, ULONG DataLength, ULONG* BytesReturned);

public:
	MediaStream()
	{
		SetBaseAttributesTraceName(L"MediaStreamAtts");
	}

	HRESULT Initialize(IMFMediaSource* source, int index);
	HRESULT SetAllocator(IUnknown* allocator);
	MFSampleAllocatorUsage GetAllocatorUsage();
	HRESULT SetD3DManager(IUnknown* manager);
	// Start/Stop/Shutdown take the stream lock themselves; the caller may hold the source lock
	// (source -> stream order) but never this stream's lock.
	HRESULT Start(IMFMediaType* type);
	HRESULT Stop();
	void Shutdown();

private:
#if _DEBUG
	int32_t query_interface_tearoff(winrt::guid const& id, void** object) const noexcept override
	{
		RETURN_HR_MSG(E_NOINTERFACE, "MediaStream QueryInterface failed on IID %s", GUID_ToStringW(id).c_str());
	}
#endif

	// What a Stop/Shutdown hands to the code that runs after the stream lock is released.
	struct Teardown
	{
		std::unique_ptr<raaicam::DeliveryClock> clock;
		winrt::com_ptr<MediaStream> clockRef; // the reference that kept us alive for the timer
	};
	static void FinishTeardown(Teardown& t); // outside the stream lock

	static void TickThunk(void* context, int64_t tickQpc);
	void OnTick(int64_t tickQpc);
	// Caller holds _lock. True at most once per error code per 5 s (per-frame error log throttle).
	bool ShouldLogErrorLocked(HRESULT hr);
	// Caller holds _lock. Fills one sample (live frame, last good frame or card). Returns the buffer HRESULT.
	HRESULT FillSampleLocked(IMFSample* sample, int64_t tickQpc, uint8_t* region);
	// Caller holds _lock. Takes the clock and its keep-alive reference out of the stream.
	void DetachClockLocked(Teardown& t);

	winrt::slim_mutex _lock;
	MF_STREAM_STATE _state = MF_STREAM_STATE_STOPPED;
	UINT32 _width = 0;
	UINT32 _height = 0;
	GUID _format = GUID_NULL;
	int _index = 0;
	wil::com_ptr_nothrow<IMFStreamDescriptor> _descriptor;
	wil::com_ptr_nothrow<IMFMediaEventQueue> _queue;
	wil::com_ptr_nothrow<IMFMediaSource> _source;
	wil::com_ptr_nothrow<IMFVideoSampleAllocatorEx> _allocator;

	// Delivery: pending RequestSample tokens (nullptr allowed), drained one per clock tick.
	std::deque<wil::com_ptr_nothrow<IUnknown>> _pending;
	bool _loggedPendingFull = false;
	std::unique_ptr<raaicam::DeliveryClock> _clock;
	winrt::com_ptr<MediaStream> _clockRef;

	// Per-stream frame state (no process-wide frame singleton).
	raaicam::FrameReader _reader;
	std::vector<uint8_t> _frame; // persistent NV12 (pitch = _width): the last good frame for Reused
	uint64_t _cardTick = 0;
	int _lastMode = -1;          // 0 = card, 1 = live (fresh or reused); transition logging only
	int64_t _qpcFreq = 1;
	uint64_t _framesDelivered = 0;

	// MF_E_SAMPLEALLOCATOR_EMPTY back-pressure: the consumer holds every sample. Only >= 1 s of it is an error.
	bool _allocEmpty = false;
	int64_t _allocEmptySinceQpc = 0;
	// Log throttle: last time each of a few distinct error codes was logged.
	struct ErrLog { HRESULT hr = S_OK; int64_t qpc = 0; };
	ErrLog _errLog[4];
	size_t _errLogNext = 0;
};
