﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Dafda.Diagnostics;
using Dafda.Outbox;
using Dafda.Producing;
using Dafda.Tests.TestDoubles;
using Newtonsoft.Json.Linq;
using OpenTelemetry.Context.Propagation;
using Xunit;

namespace Dafda.Tests.Outbox;
[Collection("Serializing")]
public class TestProducer : IClassFixture<DafdaActivitySourceFixture>
{
    private readonly DafdaActivitySourceFixture _fixture;

    public TestProducer(DafdaActivitySourceFixture fixture)
    {
        _fixture = fixture;
    }
    
    [Fact]
    public void TryDeserializePayload_ShouldDeserializeJsonStringToDictionary()
    {
        // Arrange
        string payload =
            """{"messageId":"1b13a5e1-742e-45fb-ab7a-76d547e4e327","type":"userdisabled","traceparent":"00-1f2c12212e50621b49c80175a064d193-35a6133087d5e877-01","causationId":"1b13a5e1-742e-45fb-ab7a-76d547e4e327","correlationId":"1b13a5e1-742e-45fb-ab7a-76d547e4e327","data":{"dfdsUserId":"8cfb2d2d-9113-48c8-8c8d-41ade8d7998a"}}""";

        // Act
        bool result = DafdaActivitySource.TryDeserializePayload(payload, out var payloadDictionary);

        // Assert
        Assert.True(result);
        Assert.NotNull(payloadDictionary);
        Assert.Equal("1b13a5e1-742e-45fb-ab7a-76d547e4e327", payloadDictionary["messageId"]);
        Assert.Equal("userdisabled", payloadDictionary["type"]);
        Assert.Equal("00-1f2c12212e50621b49c80175a064d193-35a6133087d5e877-01", payloadDictionary["traceparent"]);
        Assert.Equal("1b13a5e1-742e-45fb-ab7a-76d547e4e327", payloadDictionary["causationId"]);
        Assert.Equal("1b13a5e1-742e-45fb-ab7a-76d547e4e327", payloadDictionary["correlationId"]);
        Assert.Equal("{\"dfdsUserId\":\"8cfb2d2d-9113-48c8-8c8d-41ade8d7998a\"}", payloadDictionary["data"]);
    }

    [Fact]
    public async Task Creates_activity_when_producing_outbox_message()
    {
        _fixture.ResetDafdaActivitySource();
        var topic = "foo";
        var type = "bar";
        DafdaActivitySource.Propagator = new CompositeTextMapPropagator(
            new TextMapPropagator[]
            {
                new TraceContextPropagator(),
                new BaggagePropagator()
            });

        // Arrange
        var activitySource = new ActivitySource("ActivitySourceName");
        using var activity = activitySource.StartActivity("MethodType:/Path");

        var spy = new KafkaProducerSpy();
        var outboxProducer = new OutboxProducer(spy);
        var guid = Guid.NewGuid();

        var payload = $@"{{
                                ""messageId"":""{guid.ToString()}"",
                                ""type"":""{type}"",
                                ""causationId"":""1"",
                                ""correlationId"":""1"",                           
                                ""data"":{{
                                    ""id"":""dummyId""
                                    }}
                                }}";


        var outboxEntry = new OutboxEntry(
            messageId: guid,
            topic: topic,
            key: type,
            payload: payload,
            occurredUtc: DateTime.UtcNow
        );

        var activities = new List<Activity>();

        // Act
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Dafda",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => activities.Add(activity),
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(activityListener);

        await outboxProducer.Produce(outboxEntry);

        // Assert
        Assert.Equal("foo", spy.Topic);
        Assert.Equal("bar", spy.Key);
        var jsonObject = JObject.Parse(spy.Value);
        Assert.True(jsonObject["traceparent"] != null, "The JSON does not contain the traceparent element.");
        Assert.Contains(activities, a => a.DisplayName == $"Dafda.Outbox.{topic}.{type}.{OpenTelemetryMessagingOperation.Producer.Publish}");
        _fixture.ResetDafdaActivitySource();
    }

    [Fact]
    public async Task Creates_custom_activity_name_when_producing_outbox_message()
    {
        _fixture.ResetDafdaActivitySource();
        var topic = "foo";
        var type = "bar";

        // Register custom activity name function
        DafdaActivitySource.RegisterCustomActivityNameFunc(DafdaActivityType.OutboxPublishing,
            (prefix, topic, messageType, operation) => { return $"{prefix}.Custom.{topic}.{messageType}.{operation}"; });

        DafdaActivitySource.Propagator = new CompositeTextMapPropagator(
            new TextMapPropagator[]
            {
                new TraceContextPropagator(),
                new BaggagePropagator()
            });

        // Arrange
        var activitySource = new ActivitySource("ActivitySourceName");
        using var activity = activitySource.StartActivity("MethodType:/Path");

        var spy = new KafkaProducerSpy();
        var outboxProducer = new OutboxProducer(spy);
        var guid = Guid.NewGuid();

        var payload = $@"{{
                                ""messageId"":""{guid.ToString()}"",
                                ""type"":""{type}"",
                                ""causationId"":""1"",
                                ""correlationId"":""1"",                           
                                ""data"":{{
                                    ""id"":""dummyId""
                                    }}
                                }}";

        var outboxEntry = new OutboxEntry(
            messageId: guid,
            topic: topic,
            key: type,
            payload: payload,
            occurredUtc: DateTime.UtcNow
        );

        var activities = new List<Activity>();

        // Act
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Dafda",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => activities.Add(activity),
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(activityListener);

        await outboxProducer.Produce(outboxEntry);

        // Assert
        Assert.Equal("foo", spy.Topic);
        Assert.Equal("bar", spy.Key);
        var jsonObject = JObject.Parse(spy.Value);
        Assert.True(jsonObject["traceparent"] != null, "The JSON does not contain the traceparent element.");
        Assert.Contains(activities, a => a.DisplayName == $"Dafda.Outbox.Custom.{topic}.{type}.{OpenTelemetryMessagingOperation.Producer.Publish}");
        _fixture.ResetDafdaActivitySource();
    }
}