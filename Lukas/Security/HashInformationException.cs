// Lukas/Security/HashInformationException.cs

using System;

namespace Lukas.Security;

public sealed class HashInformationException(string message, Exception innerException)
    : Exception(message, innerException);
