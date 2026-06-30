// Lukas/Security/BcryptAuthenticationException.cs

using System;

namespace Lukas.Security;

public sealed class BcryptAuthenticationException(string message) : Exception(message);
