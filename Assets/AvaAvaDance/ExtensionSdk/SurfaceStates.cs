namespace Ava.Extensions
{
    // Numeric values follow D3D11 state enumerations. DepthComparison uses reversed Z.
    public sealed class SurfaceStates
    {
        public int SrcBlend=5,DestBlend=6,BlendOp=1,SrcBlendAlpha=2,DestBlendAlpha=6,BlendOpAlpha=1;
        public int DepthComparison=7,WriteMask=15,FillMode=3;
        public bool DepthEnabled=true,StencilEnabled;
        public int StencilReadMask=255,StencilWriteMask=255,StencilReference,StencilComparison=8,StencilPass=1,StencilFail=1,StencilDepthFail=1;
        internal int[] Pack()=>new[]{SrcBlend,DestBlend,BlendOp,SrcBlendAlpha,DestBlendAlpha,BlendOpAlpha,DepthEnabled?1:0,
            DepthComparison,WriteMask,FillMode,StencilEnabled?1:0,StencilReadMask,StencilWriteMask,StencilReference,StencilComparison,StencilPass,StencilFail,StencilDepthFail};
    }

    public sealed class SurfaceSampler
    {
        public int Filter=21,AddressU=1,AddressV=1,AddressW=1,Anisotropy=1;
        public float MipBias;
        internal void Pack(int[] dest,int at)
        {
            dest[at]=Filter;dest[at+1]=AddressU;dest[at+2]=AddressV;dest[at+3]=AddressW;
            dest[at+4]=Anisotropy;dest[at+5]=System.BitConverter.SingleToInt32Bits(MipBias);
        }
    }
}
