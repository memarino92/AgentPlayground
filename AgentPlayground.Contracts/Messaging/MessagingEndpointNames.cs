namespace AgentPlayground.Contracts.Messaging;

public static class MessagingEndpointNames
{
    public const string Worker = "personalagent-worker";
    public const string Agent = "personalagent-agent";
    public const string NotificationScheduler = "personal-agent-notification-scheduler";
    public const string PushNotification = "personal-agent-push-notification";
    public const string AgentTaskScheduler = "personal-agent-worker-agent-task-scheduler";
    public const string AgentTaskExecutor = "personal-agent-worker-agent-task-executor";
}
