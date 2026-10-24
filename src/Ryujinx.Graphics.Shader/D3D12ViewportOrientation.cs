namespace Ryujinx.Graphics.Shader
{
    public static class D3D12ViewportOrientation
    {
        public static float ClipXSign(float width) => width < 0f ? -1f : 1f;
        public static float ClipYSign(float height) => height < 0f ? 1f : -1f;
    }
}
