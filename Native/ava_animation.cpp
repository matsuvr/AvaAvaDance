#define NOMINMAX
#include <windows.h>
#include <objidl.h>
#include <gdiplus.h>
#include <shlwapi.h>
#include <wrl/client.h>
#include <vector>
#include <memory>
#include <cstring>
#include <cstdint>
using namespace Gdiplus;
using Microsoft::WRL::ComPtr;

struct Gif {
    ULONG_PTR token{};
    ComPtr<IStream> stream;
    std::unique_ptr<Bitmap> image;
    UINT width{},height{},count{};
    std::vector<unsigned> delays;
    ~Gif(){image.reset();stream.Reset();if(token)GdiplusShutdown(token);}
};
#define API extern "C" __declspec(dllexport)
API Gif* ava_gif_open(const BYTE* data,int length,int* width,int* height,int* frames)
{
    if(length<13 || !data || memcmp(data,"GIF8",4)!=0)return nullptr;
    try{
        auto gif=std::make_unique<Gif>();GdiplusStartupInput init;
        if(GdiplusStartup(&gif->token,&init,nullptr)!=Ok)return nullptr;
        gif->stream.Attach(SHCreateMemStream(data,length));if(!gif->stream)return nullptr;
        gif->image.reset(Bitmap::FromStream(gif->stream.Get()));auto& image=*gif->image;
        if(image.GetLastStatus()!=Ok)return nullptr;
        gif->width=image.GetWidth();gif->height=image.GetHeight();gif->count=image.GetFrameCount(&FrameDimensionTime);
        if(!gif->count || !gif->width || !gif->height || uint64_t(gif->width)*gif->height*gif->count>32u*1024*1024)return nullptr;
        gif->delays.assign(gif->count,10);UINT size=image.GetPropertyItemSize(PropertyTagFrameDelay);
        if(size>=sizeof(PropertyItem)){
            std::vector<BYTE> storage(size);auto info=reinterpret_cast<PropertyItem*>(storage.data());
            if(image.GetPropertyItem(PropertyTagFrameDelay,size,info)==Ok && info->type==PropertyTagTypeLong && info->length>=gif->count*4)
                memcpy(gif->delays.data(),info->value,gif->count*4);
        }
        *width=gif->width;*height=gif->height;*frames=gif->count;return gif.release();
    }catch(...){return nullptr;}
}
API int ava_gif_frame(Gif* gif,int frame,BYTE* rgba,int size)
{
    if(!gif || frame<0 || static_cast<UINT>(frame)>=gif->count || size<0 || static_cast<UINT>(size)!=gif->width*gif->height*4)return -1;
    if(gif->image->SelectActiveFrame(&FrameDimensionTime,frame)!=Ok)return -1;
    Rect area(0,0,gif->width,gif->height);BitmapData pixels{};
    if(gif->image->LockBits(&area,ImageLockModeRead,PixelFormat32bppARGB,&pixels)!=Ok)return -1;
    for(UINT y=0;y<gif->height;y++){
        auto src=static_cast<const BYTE*>(pixels.Scan0)+static_cast<ptrdiff_t>(y)*pixels.Stride;
        BYTE* dst=rgba+(gif->height-y-1)*gif->width*4;
        for(UINT x=0;x<gif->width;x++,src+=4,dst+=4){dst[0]=src[2];dst[1]=src[1];dst[2]=src[0];dst[3]=src[3];}
    }
    gif->image->UnlockBits(&pixels);
    unsigned ticks=gif->delays[frame];return ticks>0 && ticks<=214748364?static_cast<int>(ticks*10):100;
}
API void ava_gif_close(Gif* gif){delete gif;}
