// RaaiCam Phase 0 spike: headless capture probe for the virtual camera.
//
//   probe.exe [--name "RaaiCam Test"] [--frames 30] [--width 1280 --height 720] [--out file.bmp]
//
// Lists video capture devices (MFEnumDeviceSources), opens the named one with IMFSourceReader,
// reads N frames, prints per-frame stats, saves the last frame as a 24-bit BMP and classifies it:
//   NO_SIGNAL  = mostly Y==0x60 with a moving white block
//   LIVE       = anything else (e.g. the frame writer's color bars)
// Exit codes: 0 ok, 2 device not found / open failed, 4 timeout waiting for a frame.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <cstdio>
#include <cstdint>
#include <string>
#include <vector>
#include <thread>
#include <atomic>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")

template <class T> static void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

struct FrameStats
{
	double meanY = 0;
	double fracPattern = 0; // fraction of Y == 0x60
	int blockX = -1;        // first x of a white run on the center row (pattern block)
	uint64_t hash = 0;
};

static FrameStats Analyze(const BYTE* y, LONG pitch, UINT32 w, UINT32 h)
{
	FrameStats s;
	uint64_t sum = 0, pattern = 0;
	uint64_t hash = 1469598103934665603ull;
	for (UINT32 row = 0; row < h; row++)
	{
		auto line = y + (size_t)row * pitch;
		for (UINT32 x = 0; x < w; x++)
		{
			sum += line[x];
			pattern += line[x] == 0x60;
			if ((x & 7) == 0) { hash ^= line[x]; hash *= 1099511628211ull; }
		}
	}
	s.meanY = (double)sum / ((double)w * h);
	s.fracPattern = (double)pattern / ((double)w * h);
	s.hash = hash;
	auto center = y + (size_t)(h / 2) * pitch;
	for (UINT32 x = 0; x < w; x++)
		if (center[x] >= 0xE0) { s.blockX = (int)x; break; }
	return s;
}

static inline BYTE Clamp(int v) { return (BYTE)(v < 0 ? 0 : v > 255 ? 255 : v); }

static bool SaveBmp(const std::wstring& path, const BYTE* y, LONG pitch, UINT32 w, UINT32 h)
{
	auto uvPlane = y + (size_t)pitch * h;
	UINT32 rowBytes = (w * 3 + 3) & ~3u;
	std::vector<BYTE> pixels((size_t)rowBytes * h);
	for (UINT32 row = 0; row < h; row++)
	{
		auto dst = pixels.data() + (size_t)(h - 1 - row) * rowBytes; // bottom-up
		auto yl = y + (size_t)row * pitch;
		auto uvl = uvPlane + (size_t)(row / 2) * pitch;
		for (UINT32 x = 0; x < w; x++)
		{
			int c = yl[x] - 16, d = uvl[x & ~1u] - 128, e = uvl[(x & ~1u) + 1] - 128; // BT.601 video range
			dst[x * 3 + 2] = Clamp((298 * c + 409 * e + 128) >> 8);
			dst[x * 3 + 1] = Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
			dst[x * 3 + 0] = Clamp((298 * c + 516 * d + 128) >> 8);
		}
	}

	BITMAPFILEHEADER bf{};
	BITMAPINFOHEADER bi{};
	bi.biSize = sizeof(bi);
	bi.biWidth = (LONG)w;
	bi.biHeight = (LONG)h;
	bi.biPlanes = 1;
	bi.biBitCount = 24;
	bi.biSizeImage = (DWORD)pixels.size();
	bf.bfType = 0x4D42;
	bf.bfOffBits = sizeof(bf) + sizeof(bi);
	bf.bfSize = bf.bfOffBits + bi.biSizeImage;

	FILE* f = nullptr;
	if (_wfopen_s(&f, path.c_str(), L"wb") || !f)
		return false;
	fwrite(&bf, sizeof(bf), 1, f);
	fwrite(&bi, sizeof(bi), 1, f);
	fwrite(pixels.data(), 1, pixels.size(), f);
	fclose(f);
	return true;
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

int wmain(int argc, wchar_t** argv)
{
	setvbuf(stdout, nullptr, _IONBF, 0);
	std::wstring name = L"RaaiCam Test";
	std::wstring out = L"probe-last.bmp";
	int frames = 30;
	UINT32 wantW = 0, wantH = 0;
	for (int i = 1; i + 1 < argc; i += 2)
	{
		std::wstring a = argv[i];
		if (a == L"--name") name = argv[i + 1];
		else if (a == L"--frames") frames = _wtoi(argv[i + 1]);
		else if (a == L"--width") wantW = (UINT32)_wtoi(argv[i + 1]);
		else if (a == L"--height") wantH = (UINT32)_wtoi(argv[i + 1]);
		else if (a == L"--out") out = argv[i + 1];
	}

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
					wprintf(L"TIMEOUT: no progress for 15 s\n");
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
		wprintf(L"MFEnumDeviceSources hr=0x%08X\n", hr);
		return 2;
	}

	wprintf(L"video capture devices: %u\n", count);
	IMFActivate* chosen = nullptr;
	for (UINT32 i = 0; i < count; i++)
	{
		auto fn = GetString(devices[i], MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
		auto link = GetString(devices[i], MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
		wprintf(L"  [%u] \"%s\"  %s\n", i, fn.c_str(), link.c_str());
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
	{
		wprintf(L"DEVICE NOT FOUND: \"%s\"\n", name.c_str());
		return 2;
	}

	lastProgress = GetTickCount64();
	IMFMediaSource* source = nullptr;
	hr = chosen->ActivateObject(IID_PPV_ARGS(&source));
	if (FAILED(hr))
	{
		wprintf(L"ActivateObject hr=0x%08X\n", hr);
		return 2;
	}

	IMFSourceReader* reader = nullptr;
	hr = MFCreateSourceReaderFromMediaSource(source, nullptr, &reader);
	if (FAILED(hr))
	{
		wprintf(L"MFCreateSourceReaderFromMediaSource hr=0x%08X\n", hr);
		return 2;
	}

	const DWORD stream = (DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM;
	IMFMediaType* pick = nullptr;
	for (DWORD i = 0;; i++)
	{
		IMFMediaType* t = nullptr;
		if (FAILED(reader->GetNativeMediaType(stream, i, &t)))
			break;
		GUID sub{};
		UINT32 w = 0, h = 0, num = 0, den = 0;
		t->GetGUID(MF_MT_SUBTYPE, &sub);
		MFGetAttributeSize(t, MF_MT_FRAME_SIZE, &w, &h);
		MFGetAttributeRatio(t, MF_MT_FRAME_RATE, &num, &den);
		wprintf(L"  native type %lu: %s %ux%u @ %u/%u\n", i, sub == MFVideoFormat_NV12 ? L"NV12" : L"other", w, h, num, den);
		if (!pick && sub == MFVideoFormat_NV12 && (wantW == 0 || (w == wantW && h == wantH)))
			pick = t;
		else
			t->Release();
	}
	if (!pick)
	{
		wprintf(L"no matching NV12 native type\n");
		return 2;
	}

	UINT32 w = 0, h = 0;
	MFGetAttributeSize(pick, MF_MT_FRAME_SIZE, &w, &h);
	hr = reader->SetCurrentMediaType(stream, nullptr, pick);
	pick->Release();
	if (FAILED(hr))
	{
		wprintf(L"SetCurrentMediaType hr=0x%08X\n", hr);
		return 2;
	}
	wprintf(L"opened \"%s\" at %ux%u NV12, reading %d frames\n", name.c_str(), w, h, frames);

	std::vector<BYTE> last((size_t)w * h * 3 / 2);
	uint64_t firstHash = 0;
	int distinct = 0, got = 0, patternFrames = 0, blockMoves = 0, prevBlockX = -2, prevClass = -1;
	uint64_t prevHash = 0;
	LONGLONG firstTs = 0, lastTs = 0;
	ULONGLONG t0 = GetTickCount64();
	while (got < frames)
	{
		DWORD flags = 0;
		LONGLONG ts = 0;
		IMFSample* sample = nullptr;
		hr = reader->ReadSample(stream, 0, nullptr, &flags, &ts, &sample);
		if (FAILED(hr))
		{
			wprintf(L"ReadSample hr=0x%08X\n", hr);
			break;
		}
		if (flags & MF_SOURCE_READERF_ENDOFSTREAM)
		{
			wprintf(L"end of stream\n");
			break;
		}
		if (!sample)
			continue;
		lastProgress = GetTickCount64();

		IMFMediaBuffer* buf = nullptr;
		sample->ConvertToContiguousBuffer(&buf);
		BYTE* data = nullptr;
		DWORD len = 0;
		if (buf && SUCCEEDED(buf->Lock(&data, nullptr, &len)) && len >= last.size())
		{
			memcpy(last.data(), data, last.size());
			buf->Unlock();
			auto s = Analyze(last.data(), (LONG)w, w, h);
			if (got == 0) { firstHash = s.hash; firstTs = ts; }
			if (got == 0 || s.hash != prevHash) distinct++;
			if (s.fracPattern > 0.9) patternFrames++;
			int cls = s.fracPattern > 0.9 ? 0 : 1;
			if (cls != prevClass)
			{
				SYSTEMTIME st;
				GetLocalTime(&st);
				wprintf(L"  TRANSITION frame %3d wall=%02u:%02u:%02u.%03u -> %s\n", got, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, cls ? L"LIVE" : L"NO_SIGNAL");
				prevClass = cls;
			}
			if (prevBlockX >= -1 && s.blockX != prevBlockX) blockMoves++;
			prevBlockX = s.blockX;
			prevHash = s.hash;
			lastTs = ts;
			if (got % 10 == 0 || got == frames - 1)
				wprintf(L"  frame %3d ts=%8.3fs meanY=%6.2f pattern(Y==0x60)=%5.1f%% whiteX=%d\n", got, ts / 1e7, s.meanY, s.fracPattern * 100, s.blockX);
		}
		else
		{
			wprintf(L"  frame %d: buffer lock failed or too small (len=%lu)\n", got, len);
		}
		SafeRelease(buf);
		sample->Release();
		got++;
	}
	auto elapsed = (GetTickCount64() - t0) / 1000.0;
	done = true;

	if (got == 0)
	{
		wprintf(L"RESULT: no frames\n");
		return 4;
	}

	auto s = Analyze(last.data(), (LONG)w, w, h);
	// sample the Y/U/V at 8 bar centers on row h/4 (for color bars)
	wprintf(L"row h/4 samples (Y,U,V) at 8 bar centers:");
	auto uvPlane = last.data() + (size_t)w * h;
	for (int b = 0; b < 8; b++)
	{
		UINT32 x = (UINT32)((b * 2 + 1) * w / 16) & ~1u;
		UINT32 row = h / 4;
		wprintf(L" (%u,%u,%u)", last[(size_t)row * w + x], uvPlane[(size_t)(row / 2) * w + x], uvPlane[(size_t)(row / 2) * w + x + 1]);
	}
	wprintf(L"\n");

	bool noSignal = s.fracPattern > 0.9;
	wprintf(L"frames=%d elapsed=%.2fs (~%.1f fps wall, %.1f fps by timestamps) distinctFrames=%d patternFrames=%d whiteBlockMoves=%d\n",
		got, elapsed, got / (elapsed > 0 ? elapsed : 1), got > 1 && lastTs > firstTs ? (got - 1) / ((lastTs - firstTs) / 1e7) : 0.0, distinct, patternFrames, blockMoves);
	wprintf(L"last frame: meanY=%.2f pattern(Y==0x60)=%.1f%% whiteX=%d changedOverTime=%s\n", s.meanY, s.fracPattern * 100, s.blockX, distinct > 1 ? L"yes" : L"no");
	wprintf(L"CLASSIFICATION: %s\n", noSignal ? L"NO_SIGNAL" : L"LIVE");

	if (SaveBmp(out, last.data(), (LONG)w, w, h))
		wprintf(L"saved %s\n", out.c_str());
	else
		wprintf(L"failed to save %s\n", out.c_str());

	reader->Release();
	source->Shutdown();
	source->Release();
	chosen->ShutdownObject();
	chosen->Release();
	MFShutdown();
	return 0;
}
