namespace TelegramDownloader.Security;

internal interface ISessionCipher
{
    (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(byte[] plaintext);

    byte[] Decrypt(byte[] ciphertext, byte[] nonce, byte[] tag);
}
