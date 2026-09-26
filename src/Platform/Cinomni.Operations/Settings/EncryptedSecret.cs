namespace Cinomni.Operations.Settings;

/// <summary>
/// The ciphertext and everything needed to store, and later decrypt, one secret setting.
/// <see cref="Cipher"/> carries the AES-GCM ciphertext with its 16-byte authentication tag appended,
/// matching <c>operations.setting.secret_cipher</c>'s documented shape (one column, not two).
/// </summary>
public sealed record EncryptedSecret(byte[] Cipher, byte[] Nonce, string KeyId, string Fingerprint);
