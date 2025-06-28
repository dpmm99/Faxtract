using LLama.Native;

namespace Faxtract.Interfaces;

// Core interface for chat templating
public interface IChatTemplate
{
    /// <summary>
    /// Apply templating for a message with the specified role
    /// </summary>
    /// <param name="role">Message role (system/user/assistant)</param>
    /// <param name="message">Message content</param>
    /// <param name="includeBefore">Include tokens that come before the message</param>
    /// <param name="includeAfter">Include tokens that come after the message</param>
    /// <returns>Template result with tokens and text</returns>
    LLamaToken[] Apply(string role, string message, bool includeBefore = true, bool includeAfter = true);

    /// <summary>
    /// Get conversation start tokens (BOS)
    /// </summary>
    LLamaToken[] GetConversationStart();
}
