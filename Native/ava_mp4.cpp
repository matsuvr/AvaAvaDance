#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>
#include <algorithm>
#include <cstring>
#include <memory>
#include <new>

using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)
#define CHECK(call) do { HRESULT result = (call); if (FAILED(result)) return result; } while (0)

struct Movie {
    ComPtr<IMFSinkWriter> writer;
    DWORD video = 0, audio = 0;
    int width, height, fps;
    bool hasAudio, com = false, mf = false;
    LONGLONG frames = 0, samples = 0;
    ~Movie() { writer.Reset(); if (mf) MFShutdown(); if (com) CoUninitialize(); }
};

static HRESULT videoType(IMFMediaType* type, GUID subtype, const Movie& m) {
    CHECK(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    CHECK(type->SetGUID(MF_MT_SUBTYPE, subtype));
    CHECK(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    CHECK(MFSetAttributeSize(type, MF_MT_FRAME_SIZE, m.width, m.height));
    CHECK(MFSetAttributeRatio(type, MF_MT_FRAME_RATE, m.fps, 1));
    CHECK(MFSetAttributeRatio(type, MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    CHECK(type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT601));
    return type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
}

static HRESULT addAudio(Movie& m) {
    ComPtr<IMFMediaType> output, input;
    CHECK(MFCreateMediaType(&output));
    CHECK(output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
    CHECK(output->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC));
    CHECK(output->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2));
    CHECK(output->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000));
    CHECK(output->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
    CHECK(output->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000));
    CHECK(m.writer->AddStream(output.Get(), &m.audio));
    CHECK(MFCreateMediaType(&input));
    CHECK(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
    CHECK(input->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM));
    CHECK(input->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2));
    CHECK(input->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000));
    CHECK(input->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
    CHECK(input->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 4));
    CHECK(input->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 192000));
    return m.writer->SetInputMediaType(m.audio, input.Get(), nullptr);
}

API HRESULT ava_movie_open(const wchar_t* path, int width, int height, int fps, int bitrate, int audio, Movie** handle) {
    if (!handle || !path || width < 2 || height < 2 || width > 4096 || height > 4096 ||
        ((width | height) & 1) || fps < 1 || fps > 120 || bitrate < 1) return E_INVALIDARG;
    *handle = nullptr;
    std::unique_ptr<Movie> m(new(std::nothrow) Movie);
    if (!m) return E_OUTOFMEMORY;
    m->width = width; m->height = height; m->fps = fps; m->hasAudio = audio != 0;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) return hr;
    m->com = SUCCEEDED(hr);
    CHECK(MFStartup(MF_VERSION)); m->mf = true;
    ComPtr<IMFAttributes> attrs;
    CHECK(MFCreateAttributes(&attrs, 2));
    CHECK(attrs->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE));
    CHECK(attrs->SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4));
    CHECK(MFCreateSinkWriterFromURL(path, nullptr, attrs.Get(), &m->writer));
    ComPtr<IMFMediaType> output, input;
    CHECK(MFCreateMediaType(&output));
    CHECK(videoType(output.Get(), MFVideoFormat_H264, *m));
    CHECK(output->SetUINT32(MF_MT_AVG_BITRATE, bitrate));
    CHECK(m->writer->AddStream(output.Get(), &m->video));
    CHECK(MFCreateMediaType(&input));
    CHECK(videoType(input.Get(), MFVideoFormat_NV12, *m));
    CHECK(input->SetUINT32(MF_MT_DEFAULT_STRIDE, width));
    CHECK(m->writer->SetInputMediaType(m->video, input.Get(), nullptr));
    if (m->hasAudio) CHECK(addAudio(*m));
    CHECK(m->writer->BeginWriting());
    *handle = m.release();
    return S_OK;
}

static HRESULT send(Movie& m, DWORD stream, IMFMediaBuffer* buf, DWORD size, LONGLONG t, LONGLONG end) {
    ComPtr<IMFSample> sample;
    CHECK(buf->SetCurrentLength(size));
    CHECK(MFCreateSample(&sample));
    CHECK(sample->AddBuffer(buf));
    CHECK(sample->SetSampleTime(t));
    CHECK(sample->SetSampleDuration(end-t));
    return m.writer->WriteSample(stream, sample.Get());
}

static BYTE clampByte(int x) { return static_cast<BYTE>((std::max)(0, (std::min)(255, x))); }

API HRESULT ava_movie_video(Movie* m, const BYTE* rgba, int bytes) {
    if (!m || !rgba || bytes != m->width*m->height*4) return E_INVALIDARG;
    const int w=m->width, h=m->height, size=w*h*3/2;
    ComPtr<IMFMediaBuffer> buf;
    CHECK(MFCreateMemoryBuffer(size, &buf));
    BYTE* dest;
    CHECK(buf->Lock(&dest, nullptr, nullptr));
    // ReadPixels rows begin at the bottom. NV12 scanlines begin at the top.
    for (int y=0; y<h; y+=2) {
        for (int x=0; x<w; x+=2) {
            int r=0,g=0,b=0;
            for (int j=0;j<2;++j) for (int i=0;i<2;++i) {
                const BYTE* p=rgba+4*((h-1-y-j)*w+x+i);
                dest[(y+j)*w+x+i]=clampByte(16+((66*p[0]+129*p[1]+25*p[2]+128)>>8));
                r+=p[0]; g+=p[1]; b+=p[2];
            }
            BYTE* uv=dest+w*h+y/2*w+x;
            uv[0]=clampByte(128+((-38*r-74*g+112*b+512)>>10));
            uv[1]=clampByte(128+((112*r-94*g-18*b+512)>>10));
        }
    }
    CHECK(buf->Unlock());
    LONGLONG f=m->frames++;
    return send(*m, m->video, buf.Get(), size, f*10000000/m->fps, (f+1)*10000000/m->fps);
}

API HRESULT ava_movie_audio(Movie* m, const short* pcm, int count) {
    if (!m || !m->hasAudio || !pcm || count<=0 || count>48000) return E_INVALIDARG;
    ComPtr<IMFMediaBuffer> buf;
    DWORD size=count*4;
    CHECK(MFCreateMemoryBuffer(size, &buf));
    BYTE* p;
    CHECK(buf->Lock(&p, nullptr, nullptr)); memcpy(p,pcm,size); CHECK(buf->Unlock());
    LONGLONG start=m->samples; m->samples+=count;
    return send(*m,m->audio,buf.Get(),size,start*10000000/48000,m->samples*10000000/48000);
}

API HRESULT ava_movie_close(Movie* handle, int finish) {
    std::unique_ptr<Movie> m(handle);
    return m && finish ? m->writer->Finalize() : S_OK;
}
