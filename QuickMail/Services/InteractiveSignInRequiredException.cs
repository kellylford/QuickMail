using System;

namespace QuickMail.Services;

/// <summary>
/// Thrown when a token is needed but only <b>silent</b> acquisition was permitted and it failed — the
/// user must complete an interactive sign-in. Lets background flows (e.g. startup connect) avoid
/// opening a sign-in window under a short timeout that would tear it down mid-sign-in (#206). They
/// catch this, leave the account disconnected, and let the user initiate an (unbounded) sign-in.
/// </summary>
public sealed class InteractiveSignInRequiredException : Exception
{
    public InteractiveSignInRequiredException(string message) : base(message) { }
    public InteractiveSignInRequiredException(string message, Exception inner) : base(message, inner) { }
}
