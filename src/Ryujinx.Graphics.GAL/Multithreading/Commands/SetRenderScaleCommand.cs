namespace Ryujinx.Graphics.GAL.Multithreading.Commands
{
    struct SetRenderScaleCommand : IGALCommand, IGALCommand<SetRenderScaleCommand>
    {
        public readonly CommandType CommandType => CommandType.SetRenderScale;
        private float _value;

        public void Set(float value)
        {
            _value = value;
        }

        public static void Run(ref SetRenderScaleCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            renderer.Pipeline.RenderScale = command._value;
        }
    }
}
