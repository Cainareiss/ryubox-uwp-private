using System;

namespace Ryujinx.Graphics.D3D11;

// DXGI has no six-byte RGB half format. Keep the guest stream untouched and
// expose an eight-byte stream on a free IA slot. W is half(1), never the first
// component of the next vertex. Cache only one conversion per active attribute.
internal sealed class D3D11Half3VertexBuffer : IDisposable
{
    private readonly D3D11Device _device;
    private readonly D3D11DeviceContext _context;
    private D3D11BufferResource _source;
    private long _generation = -1;
    private int _offset, _size, _stride;
    private nint _buffer;
    private int _capacity;
    internal long ConversionCount { get; private set; }

    internal D3D11Half3VertexBuffer(D3D11Device device, D3D11DeviceContext context)
    {
        _device = device;
        _context = context;
    }

    internal nint GetBuffer(D3D11BufferResource source, int offset, int size, int stride)
    {
        if (offset < 0 || size < 0 || offset > source.Size - size || stride < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Invalid half3 vertex range.");
        if (_buffer != 0 && ReferenceEquals(_source, source) && _generation == source.Generation &&
            _offset == offset && _size == size && _stride == stride)
            return _buffer;

        int count = size < 6 ? 0 : stride == 0 ? 1 : 1 + (size - 6) / stride;
        // An undersized guest attribute reads zero. Do not read past its range.
        byte[] expanded = new byte[checked(Math.Max(1, count) * 8)];
        if (count != 0)
        {
            byte[] input = new byte[size];
            source.CopyData(offset, input); // uses the valid CPU mirror when available
            ReadOnlySpan<byte> bytes = input;
            for (int vertex = 0; vertex < count; vertex++)
            {
                bytes.Slice(vertex * stride, 6).CopyTo(expanded.AsSpan(vertex * 8, 6));
                expanded[vertex * 8 + 7] = 0x3c; // IEEE half 1.0 = 0x3c00
            }
        }
        if (_capacity != expanded.Length)
        {
            D3D11BufferDesc desc = new()
            {
                ByteWidth = (uint)expanded.Length,
                Usage = D3D11Usage.Default,
                BindFlags = D3D11BindFlags.VertexBuffer,
            };
            nint replacement = _device.CreateBuffer(in desc);
            D3D11Com.Release(_buffer);
            _buffer = replacement;
            _capacity = expanded.Length;
        }
        _context.UpdateSubresource(_buffer, expanded);
        _source = source;
        _generation = source.Generation;
        _offset = offset;
        _size = size;
        _stride = stride;
        ConversionCount++;
        return _buffer;
    }

    public void Dispose()
    {
        D3D11Com.Release(_buffer);
        _buffer = 0;
        _capacity = 0;
        _source = null;
    }
}
