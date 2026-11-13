// vcam-probe: headless capture probe for the RaaiCam virtual camera.
//
//   vcam-probe.exe --name RaaiCam --width W --height H --frames N --json out.json [--trace 1] [--expect camera]
//
// Opens the named camera with IMFSourceReader, reads N NV12 frames and classifies each one:
//   card  = placeholder card: corner Y == 0x20 and center Y in 0xA0..0xC0
//   live  = the 64-bit frame id (TestPatternSource encoding) decodes cleanly in the top Y row
//   torn  = a live frame whose bottom-row id differs from its top-row id (or does not decode)
//   other = neither card nor live (reported separately, first few logged with mean Y)
//   camera = other AND luma variance > raaicam::kCameraMinVariance (cameraFrames; meanVariance = mean over all frames)
// Prints one JSON object (also written to --json):
//   {"opened":bool,"fps":double,"cardFrames":int,"liveFrames":int,"tornFrames":int,"distinctFrameIds":int,"error":"..."|null, ...}
// stdout also carries a per-frame class TRANSITION log with wall-clock epoch milliseconds (wall_ms=).
// Exit codes: 0 ok, 2 device not found / open failed, 4 timeout / no frames.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <cstdio>
#include <cstdint>
#include <string>
#include <vector>
#include <set>
#include <thread>
#include <atomic>
#include <mutex>

#include "FrameClassifier.h"

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")

template <class T> static void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

static std::wstring g_jsonPath;
static bool g_trace = false;
static std::wstring g_expect;
static std::mutex g_outMutex;
static std::atomic<bool> g_written{ false };

struct Result
{
	bool opened = false;
	double fps = 0, fpsTs = 0;
	int frames = 0, card = 0, live = 0, torn = 0, other = 0;
	int distinct = 0, maxDupRun = 0, camera = 0;
	double varSum = 0;
	uint64_t maxIdJump = 0, maxIdBackStep = 0;
	UINT32 w = 0, h = 0;
	std::string error;
	bool hasError = false;
};

static std::string Esc(const std::string& s)
{
	std::string r;
	for (char c : s)
	{
		if (c == '"' || c == '\\') { r += '\\'; r += c; }
		else if ((unsigned char)c < 0x20) r += ' ';
		else r += c;
	}
	return r;
}

static std::string Narrow(const std::wstring& s)
{
	if (s.empty()) return {};
	int n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0, nullptr, nullptr);
	std::string r((size_t)n, '\0');
	WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), r.data(), n, nullptr, nullptr);
	return r;
}

// Prints the JSON and writes it to --json. Only the first call does anything (watchdog vs main thread).
static void EmitJson(const Result& r)
{
	std::lock_guard<std::mutex> lock(g_outMutex);
	if (g_written.exchange(true)) return;
	char buf[1280];
	snprintf(buf, sizeof(buf),
		"{\"opened\":%s,\"fps\":%.2f,\"fpsByTimestamp\":%.2f,\"frames\":%d,\"width\":%u,\"height\":%u,"
		"\"cardFrames\":%d,\"liveFrames\":%d,\"tornFrames\":%d,\"otherFrames\":%d,\"distinctFrameIds\":%d,\"maxDupRun\":%d,\"maxIdJump\":%llu,\"maxIdBackStep\":%llu,\"cameraFrames\":%d,\"meanVariance\":%.2f,\"error\":",
		r.opened ? "true" : "false", r.fps, r.fpsTs, r.frames, r.w, r.h, r.card, r.live, r.torn, r.other, r.distinct, r.maxDupRun, (unsigned long long)r.maxIdJump, (unsigned long long)r.maxIdBackStep, r.camera, r.frames > 0 ? r.varSum / r.frames : 0.0);
	std::string json = buf;
	json += r.hasError ? "\"" + Esc(r.error) + "\"" : "null";
	json += "}";
	printf("JSON: %s\n", json.c_str());
	if (!g_jsonPath.empty())
	{
		FILE* f = nullptr;
		if (!_wfopen_s(&f, g_jsonPath.c_str(), L"wb") && f)
		{
			fwrite(json.data(), 1, json.size(), f);
			fclose(f);
		}
	}
}

static int Fail(Result& r, const std::string& msg, int code)
{
	r.hasError = true;
	r.error = msg;
	printf("ERROR: %s\n", msg.c_str());
	EmitJson(r);
	return code;
}

static std::wstring GetString(IMFActivate* a, REFGUID key)
{
	WCHAR* s = nullptr;
	UINT32 len = 0;
	std::wstring r;
	if (SUCCEEDED(a->GetAllocatedString(key, &s, &len)))
	{
		r = s;
		CoTaskMemFree(s);
	}
	return r;
}

static long long WallMs()
{
	FILETIME ft;
	GetSystemTimePreciseAsFileTime(&ft);
	ULARGE_INTEGER u{};
	u.LowPart = ft.dwLowDateTime;
	u.HighPart = ft.dwHighDateTime;
	return (long long)((u.QuadPart - 116444736000000000ull) / 10000ull);
}

// Decodes the frame id from a Y row (16-px runs, 0x10 = 0, 0xEB = 1, MSB first, left-aligned when narrow).
// Returns false if any sampled run is not clearly a 0 or a 1 (so card/other frames do not decode).
static bool DecodeId(const BYTE* row, UINT32 width, uint64_t& id)
{
	UINT32 bits = width / 16 < 64 ? width / 16 : 64;
	if (bits == 0) return false;
	uint64_t v = 0;
	for (UINT32 b = 0; b < bits; b++)
	{
		BYTE y = row[b * 16 + 8];
		if (y <= 0x18) v = (v << 1);
		else if (y >= 0xE3) v = (v << 1) | 1;
		else return false;
	}
	id = bits >= 64 ? v : v << (64 - bits);
	return true;
}

static double MeanY(const BYTE* y, UINT32 w, UINT32 h)
{
	uint64_t sum = 0, n = 0;
	for (UINT32 row = 0; row < h; row += 4)
		for (UINT32 x = 0; x < w; x += 4) { sum += y[(size_t)row * w + x]; n++; }
	return n ? (double)sum / (double)n : 0;
}

enum Cls { CardCls = 0, LiveCls = 1, OtherCls = 2 };

int wmain(int argc, wchar_t** argv)
{
	setvbuf(stdout, nullptr, _IONBF, 0);
	std::wstring name = L"RaaiCam";
	int frames = 30;
	UINT32 wantW = 0, wantH = 0;
	for (int i = 1; i + 1 < argc; i += 2)
	{
		std::wstring a = argv[i];
		if (a == L"--name") name = argv[i + 1];
		else if (a == L"--frames") frames = _wtoi(argv[i + 1]);
		else if (a == L"--width") wantW = (UINT32)_wtoi(argv[i + 1]);
		else if (a == L"--height") wantH = (UINT32)_wtoi(argv[i + 1]);
		else if (a == L"--json") g_jsonPath = argv[i + 1];
		else if (a == L"--trace") g_trace = _wtoi(argv[i + 1]) != 0;
		else if (a == L"--expect") g_expect = argv[i + 1];  // "camera": informational; cameraFrames is always reported
	}

	Result r;

	// watchdog: a hung ReadSample must not hang the probe
	std::atomic<ULONGLONG> lastProgress = GetTickCount64();
	std::atomic<bool> done = false;
	std::thread([&]
		{
			while (!done)
			{
				Sleep(500);
				if (GetTickCount64() - lastProgress > 15000)
				{
					Result t = r;
					t.hasError = true;
					t.error = "timeout: no progress for 15 s";
					printf("TIMEOUT: no progress for 15 s\n");
					EmitJson(t);
					ExitProcess(4);
				}
			}
		}).detach();

	CoInitializeEx(nullptr, COINIT_MULTITHREADED);
	MFStartup(MF_VERSION);

	IMFAttributes* attrs = nullptr;
	MFCreateAttributes(&attrs, 1);
	attrs->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
	IMFActivate** devices = nullptr;
	UINT32 count = 0;
	auto hr = MFEnumDeviceSources(attrs, &devices, &count);
	SafeRelease(attrs);
	if (FAILED(hr))
	{
		char b[64];
		snprintf(b, sizeof(b), "MFEnumDeviceSources hr=0x%08X", (unsigned)hr);
		return Fail(r, b, 2);
	}

	printf("video capture devices: %u\n", count);
	IMFActivate* chosen = nullptr;
	for (UINT32 i = 0; i < count; i++)
	{
		auto fn = GetString(devices[i], MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
		wprintf(L"  [%u] \"%s\"\n", i, fn.c_str());
		// Windows lists software virtual cameras as "<name> (Windows Virtual Camera)"
		if (!chosen && fn.compare(0, name.size(), name) == 0)
		{
			chosen = devices[i];
			chosen->AddRef();
		}
	}
	for (UINT32 i = 0; i < count; i++)
		devices[i]->Release();
	CoTaskMemFree(devices);

	if (!chosen)
		return Fail(r, "device not found: " + Narrow(name), 2);

	lastProgress = GetTickCount64();
	IMFMediaSource* source = nullptr;
	hr = chosen->ActivateObject(IID_PPV_ARGS(&source));
	if (FAILED(hr))
	{
		char b[64];
		snprintf(b, sizeof(b), "ActivateObject hr=0x%08X", (unsigned)hr);
		return Fail(r, b, 2);
	}

	IMFSourceReader* reader = nullptr;
	hr = MFCreateSourceReaderFromMediaSource(source, nullptr, &reader);
	if (FAILED(hr))
	{
		char b[80];
		snprintf(b, sizeof(b), "MFCreateSourceReaderFromMediaSource hr=0x%08X", (unsigned)hr);
		return Fail(r, b, 2);
	}

	const DWORD stream = (DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM;
	IMFMediaType* pick = nullptr;
	for (DWORD i = 0;; i++)
	{
		IMFMediaType* t = nullptr;
		if (FAILED(reader->GetNativeMediaType(stream, i, &t)))
			break;
		GUID sub{};
		UINT32 tw = 0, th = 0;
		t->GetGUID(MF_MT_SUBTYPE, &sub);
		MFGetAttributeSize(t, MF_MT_FRAME_SIZE, &tw, &th);
		if (!pick && sub == MFVideoFormat_NV12 && (wantW == 0 || (tw == wantW && th == wantH)))
			pick = t;
		else
			t->Release();
	}
	if (!pick)
		return Fail(r, "no matching NV12 native type", 2);

	UINT32 w = 0, h = 0;
	MFGetAttributeSize(pick, MF_MT_FRAME_SIZE, &w, &h);
	hr = reader->SetCurrentMediaType(stream, nullptr, pick);
	pick->Release();
	if (FAILED(hr))
	{
		char b[64];
		snprintf(b, sizeof(b), "SetCurrentMediaType hr=0x%08X", (unsigned)hr);
		return Fail(r, b, 2);
	}
	r.opened = true;
	r.w = w;
	r.h = h;
	printf("opened at %ux%u NV12, reading %d frames\n", w, h, frames);

	std::vector<BYTE> cur((size_t)w * h * 3 / 2);
	std::set<uint64_t> ids;
	int got = 0, prevCls = -1, otherLogged = 0;
	bool havePrevId = false;
	uint64_t prevLiveId = 0;
	int dupRun = 0;
	LONGLONG firstTs = 0, lastTs = 0;
	LARGE_INTEGER qf{}, q0{}, q1{};
	QueryPerformanceFrequency(&qf);
	while (got < frames)
	{
		DWORD flags = 0;
		LONGLONG ts = 0;
		IMFSample* sample = nullptr;
		hr = reader->ReadSample(stream, 0, nullptr, &flags, &ts, &sample);
		if (FAILED(hr))
		{
			char b[64];
			snprintf(b, sizeof(b), "ReadSample hr=0x%08X", (unsigned)hr);
			r.hasError = true;
			r.error = b;
			printf("ERROR: %s\n", b);
			break;
		}
		if (flags & MF_SOURCE_READERF_ENDOFSTREAM)
		{
			r.hasError = true;
			r.error = "end of stream";
			break;
		}
		if (!sample)
			continue;
		lastProgress = GetTickCount64();
		long long wall = WallMs();
		LARGE_INTEGER qNow{};
		QueryPerformanceCounter(&qNow);

		IMFMediaBuffer* buf = nullptr;
		sample->ConvertToContiguousBuffer(&buf);
		BYTE* data = nullptr;
		DWORD len = 0;
		if (buf && SUCCEEDED(buf->Lock(&data, nullptr, &len)) && len >= cur.size())
		{
			memcpy(cur.data(), data, cur.size());
			buf->Unlock();
			if (got == 0) { q0 = qNow; firstTs = ts; }
			q1 = qNow;
			lastTs = ts;

			const BYTE* y = cur.data();
			const BYTE corner = y[0];
			const BYTE center = y[(size_t)(h / 2) * w + w / 2];
			uint64_t topId = 0, botId = 0;
			int cls;
			if (DecodeId(y, w, topId))
			{
				cls = LiveCls;
				r.live++;
				ids.insert(topId);
				if (havePrevId && topId == prevLiveId) dupRun++;
				else
				{
					dupRun = 1;
					if (havePrevId && topId > prevLiveId && topId - prevLiveId > r.maxIdJump) r.maxIdJump = topId - prevLiveId;
					if (havePrevId && topId < prevLiveId && prevLiveId - topId > r.maxIdBackStep) r.maxIdBackStep = prevLiveId - topId;
				}
				if (dupRun > r.maxDupRun) r.maxDupRun = dupRun;
				havePrevId = true;
				prevLiveId = topId;
				if (!DecodeId(y + (size_t)(h - 1) * w, w, botId) || botId != topId)
					r.torn++;
			}
			else if (raaicam::IsCardSignature(y, w, h))
			{
				cls = CardCls;
				r.card++;
				havePrevId = false;
			}
			else
			{
				cls = OtherCls;
				r.other++;
				havePrevId = false;
				if (otherLogged++ < 5)
					printf("  OTHER frame %d meanY=%.2f corner=0x%02X center=0x%02X\n", got, MeanY(y, w, h), corner, center);
			}
			const double variance = raaicam::LumaVariance(y, w, h);
			r.varSum += variance;
			if (raaicam::IsCameraFrame(cls == CardCls, cls == LiveCls, variance)) r.camera++;
			if (g_trace)
				printf("  F %d cls=%d id=%llu wall_ms=%lld ts=%lld\n", got, cls, (unsigned long long)topId, wall, (long long)ts);
			if (cls != prevCls)
			{
				printf("  TRANSITION frame=%d wall_ms=%lld -> %s\n", got, wall, cls == LiveCls ? "LIVE" : cls == CardCls ? "CARD" : "OTHER");
				prevCls = cls;
			}
		}
		else
		{
			printf("  frame %d: buffer lock failed or too small (len=%lu)\n", got, len);
		}
		SafeRelease(buf);
		sample->Release();
		got++;
	}
	done = true;

	r.frames = got;
	r.distinct = (int)ids.size();
	if (got > 1)
	{
		double secs = (double)(q1.QuadPart - q0.QuadPart) / (double)qf.QuadPart;
		r.fps = secs > 0 ? (got - 1) / secs : 0;
		r.fpsTs = lastTs > firstTs ? (got - 1) / ((lastTs - firstTs) / 1e7) : 0;
	}
	if (got == 0 && !r.hasError)
	{
		r.hasError = true;
		r.error = "no frames";
	}
	EmitJson(r);

	reader->Release();
	source->Shutdown();
	source->Release();
	chosen->ShutdownObject();
	chosen->Release();
	MFShutdown();
	return got == 0 ? 4 : 0;
}
