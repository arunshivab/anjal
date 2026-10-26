using System.Buffers.Binary;
using System.Text;

namespace Anjal.Store;

/// <summary>
/// rc.25 (DES-15 §7.10): a large file kept encrypted on disk - a person's data download - written
/// and read in pieces, so that a mailbox of several gigabytes is never held in memory whole. Each
/// piece of up to 1 MiB is its own envelope (<see cref="MailEnvelope"/>); the file ends with an
/// empty piece, so a file cut short is found.
/// <code>
/// "ANJX" | version 1 | ( piece length (u32, big-endian) | envelope ) ... | 0 (u32)
/// </code>
/// </summary>
public static class SealedFile
{
    /// <summary>The largest piece of plain bytes in one envelope.</summary>
    public const int PieceSize = 1 << 20;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ANJX");

    /// <summary>A stream that encrypts what is written to it into <paramref name="inner"/>, piece by piece, with a symmetric key.</summary>
    /// <param name="inner">Where the encrypted file goes (left open).</param>
    /// <param name="key">The 256-bit key.</param>
    /// <returns>The writing stream; dispose it to finish the file.</returns>
    public static Stream Writer(Stream inner, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(key);
        return new SealingStream(inner, (byte[])key.Clone());
    }

    /// <summary>Whether a file begins as an encrypted file.</summary>
    /// <param name="head">Its first bytes.</param>
    /// <returns>Whether it does.</returns>
    public static bool IsSealed(ReadOnlySpan<byte> head) => head.Length >= 5 && head[..4].SequenceEqual(Magic) && head[4] == 1;

    /// <summary>Decrypt an encrypted file into <paramref name="output"/>.</summary>
    /// <param name="input">The encrypted file.</param>
    /// <param name="output">Where the plain bytes go.</param>
    /// <param name="keys">The keys that open it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidDataException">The file is not whole, or does not open with these keys.</exception>
    public static async Task OpenToAsync(Stream input, Stream output, IMailKeySource keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(keys);
        byte[] head = new byte[5];
        await input.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        if (!IsSealed(head))
        {
            throw new InvalidDataException("Not an encrypted file.");
        }
        byte[] lengthBytes = new byte[4];
        while (true)
        {
            await input.ReadExactlyAsync(lengthBytes, ct).ConfigureAwait(false);
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
            if (length == 0)
            {
                return;
            }
            if (length < 0 || length > PieceSize + 4096)
            {
                throw new InvalidDataException("A piece of the file is damaged.");
            }
            byte[] piece = new byte[length];
            await input.ReadExactlyAsync(piece, ct).ConfigureAwait(false);
            byte[] plain = MailEnvelope.Open(piece, keys) ?? throw new InvalidDataException("The file does not open with these keys.");
            await output.WriteAsync(plain, ct).ConfigureAwait(false);
        }
    }

    private sealed class SealingStream : Stream
    {
        private readonly Stream inner;
        private readonly byte[] key;
        private readonly MemoryStream buffer = new();
        private bool started;
        private bool finished;

        public SealingStream(Stream inner, byte[] key)
        {
            this.inner = inner;
            this.key = key;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !this.finished;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            // Pieces are written when full and at the end; a flush keeps the piece size.
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(this.finished, this);
            while (buffer.Length > 0)
            {
                int room = PieceSize - (int)this.buffer.Length;
                int take = Math.Min(room, buffer.Length);
                this.buffer.Write(buffer[..take]);
                buffer = buffer[take..];
                if (this.buffer.Length == PieceSize)
                {
                    this.WritePiece();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !this.finished)
            {
                if (this.buffer.Length > 0 || !this.started)
                {
                    this.WritePiece();
                }
                Span<byte> end = stackalloc byte[4];
                this.inner.Write(end);
                this.inner.Flush();
                this.finished = true;
                this.buffer.Dispose();
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(this.key);
            }
            base.Dispose(disposing);
        }

        private void WritePiece()
        {
            if (!this.started)
            {
                this.inner.Write(Magic);
                this.inner.WriteByte(1);
                this.started = true;
            }
            if (this.buffer.Length == 0)
            {
                return;
            }
            byte[] piece = MailEnvelope.SealWithKey(this.buffer.GetBuffer().AsSpan(0, (int)this.buffer.Length), this.key);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)piece.Length);
            this.inner.Write(length);
            this.inner.Write(piece);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(this.buffer.GetBuffer());
            this.buffer.SetLength(0);
        }
    }
}
