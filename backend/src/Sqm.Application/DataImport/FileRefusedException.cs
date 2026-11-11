namespace Sqm.Application.DataImport;

/// <summary>
/// A file that arrived cannot be taken, and the reason is the sender's to act on.
/// </summary>
/// <remarks>
/// Its message is written for the person who sent the file and is shown to them as it is: an
/// archive that needs a password, one that holds no CSV, one too large to unpack. Nothing of the
/// file is kept when this is thrown. Distinguished from every other exception so that the upload
/// answers 400 with the reason, where a fault of the platform's own answers 500 without one.
/// </remarks>
public sealed class FileRefusedException : Exception
{
    public FileRefusedException(string message) : base(message)
    {
    }

    public FileRefusedException(string message, Exception inner) : base(message, inner)
    {
    }

    public FileRefusedException()
    {
    }
}
