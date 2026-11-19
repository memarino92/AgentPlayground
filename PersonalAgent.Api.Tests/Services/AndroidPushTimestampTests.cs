using System.Globalization;

using Newtonsoft.Json.Linq;
using PersonalAgent.Api.Services;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

public class AndroidPushTimestampTests
{
    [Fact]
    public void FirebasePayloadUsesCurrentUtcSendTime()
    {
        var before = DateTime.UtcNow;
        var notification = PushNotificationService.CreateAndroidNotification("approvals");
        var after = DateTime.UtcNow;
        var payload = JObject.FromObject(notification);
        var expected = notification.EventTimestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff000'Z'", CultureInfo.InvariantCulture);

        Assert.InRange(notification.EventTimestamp, before, after);
        Assert.Equal(DateTimeKind.Utc, notification.EventTimestamp.Kind);
        Assert.Equal(expected, (string?)payload["event_time"]);
        Assert.Equal("approvals", (string?)payload["channel_id"]);
    }
}
