using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Noise
{
	/// <summary>
	/// A <see href="https://noiseprotocol.org/noise.html#the-handshakestate-object">HandshakeState</see>
	/// object contains a <see href="https://noiseprotocol.org/noise.html#the-symmetricstate-object">SymmetricState</see>
	/// plus the local and remote keys (any of which may be empty),
	/// a boolean indicating the initiator or responder role, and
	/// the remaining portion of the handshake pattern.
	/// </summary>
	public interface HandshakeState : IDisposable
	{
		/// <summary>
		/// Performs the next step of the handshake,
		/// encrypts the <paramref name="payload"/>,
		/// and writes the result into <paramref name="messageBuffer"/>.
		/// The result is undefined if the <paramref name="payload"/>
		/// and <paramref name="messageBuffer"/> overlap.
		/// </summary>
		/// <param name="payload">The payload to encrypt.</param>
		/// <param name="messageBuffer">The buffer for the encrypted message.</param>
		/// <returns>
		/// The tuple containing the ciphertext size in bytes,
		/// the handshake hash, and the <see cref="Transport"/>
		/// object for encrypting transport messages. If the
		/// handshake is still in progress, the handshake hash
		/// and the transport will both be null.
		/// </returns>
		/// <exception cref="ObjectDisposedException">
		/// Thrown if the current instance has already been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// Thrown if the call to <see cref="ReadMessage"/> was expected
		/// or the handshake has already been completed.
		/// </exception>
		/// <exception cref="ArgumentException">
		/// Thrown if the encrypted payload was greater than <see cref="Protocol.MaxMessageLength"/>
		/// bytes in length, or if the output buffer did not have enough space to hold the ciphertext.
		/// </exception>
		(int BytesWritten, byte[] HandshakeHash, Transport Transport) WriteMessage(
			ReadOnlySpan<byte> payload,
			Span<byte> messageBuffer
		);

		/// <summary>
		/// Performs the next step of the handshake,
		/// decrypts the <paramref name="message"/>,
		/// and writes the result into <paramref name="payloadBuffer"/>.
		/// The result is undefined if the <paramref name="message"/>
		/// and <paramref name="payloadBuffer"/> overlap.
		/// </summary>
		/// <param name="message">The message to decrypt.</param>
		/// <param name="payloadBuffer">The buffer for the decrypted payload.</param>
		/// <returns>
		/// The tuple containing the plaintext size in bytes,
		/// the handshake hash, and the <see cref="Transport"/>
		/// object for encrypting transport messages. If the
		/// handshake is still in progress, the handshake hash
		/// and the transport will both be null.
		/// </returns>
		/// <exception cref="ObjectDisposedException">
		/// Thrown if the current instance has already been disposed.
		/// </exception>
		/// <exception cref="InvalidOperationException">
		/// Thrown if the call to <see cref="WriteMessage"/> was expected
		/// or the handshake has already been completed.
		/// </exception>
		/// <exception cref="ArgumentException">
		/// Thrown if the message was greater than <see cref="Protocol.MaxMessageLength"/>
		/// bytes in length, or if the output buffer did not have enough space to hold the plaintext.
		/// </exception>
		/// <exception cref="System.Security.Cryptography.CryptographicException">
		/// Thrown if the decryption of the message has failed.
		/// </exception>
		(int BytesRead, byte[] HandshakeHash, Transport Transport) ReadMessage(
			ReadOnlySpan<byte> message,
			Span<byte> payloadBuffer
		);
	}

	internal sealed class HandshakeState<CipherType, DhType, HashType> : HandshakeState
		where CipherType : Cipher, new()
		where DhType : Dh, new()
		where HashType : Hash, new()
	{
		private Dh dh = new DhType();
		private readonly SymmetricState<CipherType, DhType, HashType> state;
		private readonly bool initiator;
		private bool turnToWrite;
		private KeyPair e;
		private KeyPair s;
		private byte[] re;
		private byte[] rs;
		private readonly bool isOneWay;
		private readonly bool isPsk;
		private readonly Queue<MessagePattern> messagePatterns = new Queue<MessagePattern>();
		private readonly Queue<byte[]> psks = new Queue<byte[]>();
		private bool disposed;

		public HandshakeState(
			Protocol protocol,
			bool initiator,
			ReadOnlySpan<byte> prologue,
			ReadOnlySpan<byte> s,
			ReadOnlySpan<byte> rs,
			IEnumerable<byte[]> psks)
		{
			Debug.Assert(psks != null);

			if (!s.IsEmpty && s.Length != dh.DhLen)
			{
				throw new ArgumentException("Invalid local static private key.", nameof(s));
			}

			if (!rs.IsEmpty && rs.Length != dh.DhLen)
			{
				throw new ArgumentException("Invalid remote static public key.", nameof(rs));
			}

			if (s.IsEmpty && protocol.HandshakePattern.LocalStaticRequired(initiator))
			{
				throw new ArgumentException("Local static private key required, but not provided.", nameof(s));
			}

			if (!s.IsEmpty && !protocol.HandshakePattern.LocalStaticRequired(initiator))
			{
				throw new ArgumentException("Local static private key provided, but not required.", nameof(s));
			}

			if (rs.IsEmpty && protocol.HandshakePattern.RemoteStaticRequired(initiator))
			{
				throw new ArgumentException("Remote static public key required, but not provided.", nameof(rs));
			}

			if (!rs.IsEmpty && !protocol.HandshakePattern.RemoteStaticRequired(initiator))
			{
				throw new ArgumentException("Remote static public key provided, but not required.", nameof(rs));
			}

			state = new SymmetricState<CipherType, DhType, HashType>(protocol.Name);
			state.MixHash(prologue);

			this.initiator = initiator;
			this.turnToWrite = initiator;
			this.s = s.IsEmpty ? null : dh.GenerateKeyPair(s);
			this.rs = rs.IsEmpty ? null : rs.ToArray();

			ProcessPreMessages(protocol.HandshakePattern);
			ProcessPreSharedKeys(protocol, psks);

			isOneWay = messagePatterns.Count == 1;
			isPsk = protocol.Modifiers != PatternModifiers.None;
		}

		private void ProcessPreMessages(HandshakePattern handshakePattern)
		{
			foreach (var preMessage in handshakePattern.Initiator.Tokens)
			{
				if (preMessage == Token.S)
				{
					state.MixHash(initiator ? this.s.PublicKey : rs);
				}
			}

			foreach (var preMessage in handshakePattern.Responder.Tokens)
			{
				if (preMessage == Token.S)
				{
					state.MixHash(initiator ? rs : this.s.PublicKey);
				}
			}
		}

		private void ProcessPreSharedKeys(Protocol protocol, IEnumerable<byte[]> psks)
		{
			var patterns = protocol.HandshakePattern.Patterns;
			var modifiers = protocol.Modifiers;
			var position = 0;

			using (var enumerator = psks.GetEnumerator())
			{
				foreach (var pattern in patterns)
				{
					var modified = pattern;

					if (position == 0 && modifiers.HasFlag(PatternModifiers.Psk0))
					{
						modified = modified.PrependPsk();
						ProcessPreSharedKey(enumerator);
					}

					if (((int)modifiers & ((int)PatternModifiers.Psk1 << position)) != 0)
					{
						modified = modified.AppendPsk();
						ProcessPreSharedKey(enumerator);
					}

					messagePatterns.Enqueue(modified);
					++position;
				}

				if (enumerator.MoveNext())
				{
					throw new ArgumentException("Number of pre-shared keys was greater than the number of PSK modifiers.");
				}
			}
		}

		private void ProcessPreSharedKey(IEnumerator<byte[]> enumerator)
		{
			if (!enumerator.MoveNext())
			{
				throw new ArgumentException("Number of pre-shared keys was less than the number of PSK modifiers.");
			}

			var psk = enumerator.Current;

			if (psk.Length != Aead.KeySize)
			{
				throw new ArgumentException($"Pre-shared keys must be {Aead.KeySize} bytes in length.");
			}

			psks.Enqueue(psk.AsSpan().ToArray());
		}

		/// <summary>
		/// Overrides the DH function. It should only be used
		/// from Noise.Tests to fix the ephemeral private key.
		/// </summary>
		internal void SetDh(Dh dh)
		{
			this.dh = dh;
		}

		public (int, byte[], Transport) WriteMessage(ReadOnlySpan<byte> payload, Span<byte> messageBuffer)
		{
			Exceptions.ThrowIfDisposed(disposed, nameof(HandshakeState<CipherType, DhType, HashType>));

			if (messagePatterns.Count == 0)
			{
				throw new InvalidOperationException("Cannot call WriteMessage after the handshake has already been completed.");
			}

			var overhead = messagePatterns.Peek().Overhead(dh.DhLen, state.HasKey(), isPsk);
			var ciphertextSize = payload.Length + overhead;

			if (ciphertextSize > Protocol.MaxMessageLength)
			{
				throw new ArgumentException($"Noise message must be less than or equal to {Protocol.MaxMessageLength} bytes in length.");
			}

			if (ciphertextSize > messageBuffer.Length)
			{
				throw new ArgumentException("Message buffer does not have enough space to hold the ciphertext.");
			}

			if (!turnToWrite)
			{
				throw new InvalidOperationException("Unexpected call to WriteMessage (should be ReadMessage).");
			}

			var next = messagePatterns.Dequeue();
			var messageBufferLength = messageBuffer.Length;

			foreach (var token in next.Tokens)
			{
				switch (token)
				{
					case Token.E: messageBuffer = WriteE(messageBuffer); break;
					case Token.S: messageBuffer = WriteS(messageBuffer); break;
					case Token.EE: DhAndMixKey(e, re); break;
					case Token.ES: ProcessES(); break;
					case Token.SE: ProcessSE(); break;
					case Token.SS: DhAndMixKey(s, rs); break;
					case Token.PSK: ProcessPSK(); break;
				}
			}

			int bytesWritten = state.EncryptAndHash(payload, messageBuffer);
			int size = messageBufferLength - messageBuffer.Length + bytesWritten;

			Debug.Assert(ciphertextSize == size);

			byte[] handshakeHash = null;
			Transport transport = null;

			if (messagePatterns.Count == 0)
			{
				(handshakeHash, transport) = Split();
			}

			turnToWrite = false;
			return (ciphertextSize, handshakeHash, transport);
		}

		private Span<byte> WriteE(Span<byte> buffer)
		{
			Debug.Assert(e == null);

			e = dh.GenerateKeyPair();
			e.PublicKey.CopyTo(buffer);
			state.MixHash(e.PublicKey);

			if (isPsk)
			{
				state.MixKey(e.PublicKey);
			}

			return buffer.Slice(e.PublicKey.Length);
		}

		private Span<byte> WriteS(Span<byte> buffer)
		{
			Debug.Assert(s != null);

			var bytesWritten = state.EncryptAndHash(s.PublicKey, buffer);
			return buffer.Slice(bytesWritten);
		}

		public (int, byte[], Transport) ReadMessage(ReadOnlySpan<byte> message, Span<byte> payloadBuffer)
		{
			Exceptions.ThrowIfDisposed(disposed, nameof(HandshakeState<CipherType, DhType, HashType>));

			if (messagePatterns.Count == 0)
			{
				throw new InvalidOperationException("Cannot call WriteMessage after the handshake has already been completed.");
			}

			var overhead = messagePatterns.Peek().Overhead(dh.DhLen, state.HasKey(), isPsk);
			var plaintextSize = message.Length - overhead;

			if (message.Length > Protocol.MaxMessageLength)
			{
				throw new ArgumentException($"Noise message must be less than or equal to {Protocol.MaxMessageLength} bytes in length.");
			}

			if (message.Length < overhead)
			{
				throw new ArgumentException($"Noise message must be greater than {overhead} bytes in length.");
			}

			if (plaintextSize > payloadBuffer.Length)
			{
				throw new ArgumentException("Payload buffer does not have enough space to hold the plaintext.");
			}

			if (turnToWrite)
			{
				throw new InvalidOperationException("Unexpected call to ReadMessage (should be WriteMessage).");
			}

			var next = messagePatterns.Dequeue();
			var messageLength = message.Length;

			foreach (var token in next.Tokens)
			{
				switch (token)
				{
					case Token.E: message = ReadE(message); break;
					case Token.S: message = ReadS(message); break;
					case Token.EE: DhAndMixKey(e, re); break;
					case Token.ES: ProcessES(); break;
					case Token.SE: ProcessSE(); break;
					case Token.SS: DhAndMixKey(s, rs); break;
					case Token.PSK: ProcessPSK(); break;
				}
			}

			int bytesRead = state.DecryptAndHash(message, payloadBuffer);
			Debug.Assert(bytesRead == plaintextSize);

			byte[] handshakeHash = null;
			Transport transport = null;

			if (messagePatterns.Count == 0)
			{
				(handshakeHash, transport) = Split();
			}

			turnToWrite = true;
			return (plaintextSize, handshakeHash, transport);
		}

		private ReadOnlySpan<byte> ReadE(ReadOnlySpan<byte> buffer)
		{
			Debug.Assert(re == null);

			re = buffer.Slice(0, dh.DhLen).ToArray();
			state.MixHash(re);

			if (isPsk)
			{
				state.MixKey(re);
			}

			return buffer.Slice(re.Length);
		}

		private ReadOnlySpan<byte> ReadS(ReadOnlySpan<byte> message)
		{
			Debug.Assert(rs == null);

			var length = state.HasKey() ? dh.DhLen + Aead.TagSize : dh.DhLen;
			var temp = message.Slice(0, length);

			rs = new byte[dh.DhLen];
			state.DecryptAndHash(temp, rs);

			return message.Slice(length);
		}

		private void ProcessES()
		{
			if (initiator)
			{
				DhAndMixKey(e, rs);
			}
			else
			{
				DhAndMixKey(s, re);
			}
		}

		private void ProcessSE()
		{
			if (initiator)
			{
				DhAndMixKey(s, re);
			}
			else
			{
				DhAndMixKey(e, rs);
			}
		}

		private void ProcessPSK()
		{
			var psk = psks.Dequeue();
			state.MixKeyAndHash(psk);
			Utilities.ZeroMemory(psk);
		}

		private (byte[], Transport) Split()
		{
			var (c1, c2) = state.Split();

			if (isOneWay)
			{
				c2.Dispose();
				c2 = null;
			}

			Debug.Assert(psks.Count == 0);

			var handshakeHash = state.GetHandshakeHash();
			var transport = new Transport<CipherType>(initiator, c1, c2);

			Clear();

			return (handshakeHash, transport);
		}

		private void DhAndMixKey(KeyPair keyPair, ReadOnlySpan<byte> publicKey)
		{
			Debug.Assert(keyPair != null);
			Debug.Assert(!publicKey.IsEmpty);

			Span<byte> sharedKey = stackalloc byte[dh.DhLen];
			dh.Dh(keyPair, publicKey, sharedKey);
			state.MixKey(sharedKey);
		}

		private void Clear()
		{
			state.Dispose();
			e?.Dispose();
			s?.Dispose();

			foreach (var psk in psks)
			{
				Utilities.ZeroMemory(psk);
			}
		}

		public void Dispose()
		{
			if (!disposed)
			{
				Clear();
				disposed = true;
			}
		}
	}
}
