namespace PersonalAgent.Models;

internal record SessionConversation(string SessionId, string ModelId, List<ConversationMessage> Messages);
