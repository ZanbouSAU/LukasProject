// Lukas/Security/SaltParseException.cs

using System;

namespace Lukas.Security;

public sealed class SaltParseException(string message) : Exception(message);
