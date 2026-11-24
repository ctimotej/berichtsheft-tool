namespace Berichtsheft.Core;

/// <summary>Domain error with a German, user-facing message. The UI shows <see cref="Exception.Message"/> as is.</summary>
public class AppError : Exception
{
    public AppError(string message) : base(message) { }
    public AppError(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Invalid user input (validation, impossible request).</summary>
public class InvalidInputError : AppError
{
    public string Field { get; }
    public InvalidInputError(string message, string field = "") : base(message) => Field = field;
}

public class NotFoundError : AppError { public NotFoundError(string message) : base(message) { } }

public class ExportError : AppError
{
    public ExportError(string message) : base(message) { }
    public ExportError(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Template cannot be read or processed (also: a document cannot be signed).</summary>
public class TemplateError : AppError
{
    public TemplateError(string message) : base(message) { }
    public TemplateError(string message, Exception inner) : base(message, inner) { }
}

public class StorageError : AppError
{
    public StorageError(string message) : base(message) { }
    public StorageError(string message, Exception inner) : base(message, inner) { }
}

public class SignatureError : AppError { public SignatureError(string message) : base(message) { } }

/// <summary>The snippet pool is too small for the requested number of entries.</summary>
public class NotEnoughSnippetsError : AppError { public NotEnoughSnippetsError(string message) : base(message) { } }

/// <summary>An optional feature needs software that is not installed (e.g. PDF needs Word or LibreOffice).</summary>
public class FeatureUnavailableError : AppError { public FeatureUnavailableError(string message) : base(message) { } }
