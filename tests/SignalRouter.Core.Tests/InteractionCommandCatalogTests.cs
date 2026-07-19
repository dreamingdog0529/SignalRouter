using System.Buffers;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace SignalRouter.Core.Tests;

public sealed class InteractionCommandCatalogTests
{
    [Test]
    public void MvpCodecsRoundTripConcreteStructCommandsInExplicitJsonOrder()
    {
        var catalog = InteractionCommandCatalog.CreateMvp();
        using var clickJson = JsonDocument.Parse("{}");
        using var valueJson = JsonDocument.Parse("""{"value":"Wanwan"}""");

        var click = catalog.Decode(
            "click",
            1,
            "menu.start",
            clickJson.RootElement);
        var setValue = catalog.Decode(
            "set_value",
            1,
            "profile.name",
            valueJson.RootElement);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(click.Command.GetType(), Is.EqualTo(typeof(ClickCommand)));
            Assert.That(
                click.GetCommand<ClickCommand>(),
                Is.EqualTo(new ClickCommand("menu.start")));
            Assert.That(setValue.Command.GetType(), Is.EqualTo(typeof(SetValueCommand)));
            Assert.That(
                setValue.GetCommand<SetValueCommand>(),
                Is.EqualTo(new SetValueCommand("profile.name", "Wanwan")));
            Assert.That(WriteArguments(click), Is.EqualTo("{}"));
            Assert.That(WriteArguments(setValue), Is.EqualTo("""{"value":"Wanwan"}"""));
        });
    }

    [TestCase("[]")]
    [TestCase("""{"unknown":true}""")]
    [TestCase("{}")]
    [TestCase("""{"value":null}""")]
    [TestCase("""{"value":1}""")]
    [TestCase("""{"value":"first","value":"second"}""")]
    public void SetValueCodecRejectsNonObjectUnknownMissingWrongTypeAndDuplicateJson(
        string json)
    {
        var catalog = InteractionCommandCatalog.CreateMvp();
        using var document = JsonDocument.Parse(json);

        var exception = NUnitCompat.Throws<InteractionCommandException>(
            () => catalog.Decode(
                "set_value",
                1,
                "profile.name",
                document.RootElement));

        Assert.That(
            exception!.RejectionCode,
            Is.EqualTo(InteractionRejectionCode.InvalidArguments));
    }

    [Test]
    public void ClickCodecRejectsUnknownArgumentsAndInvalidTarget()
    {
        var catalog = InteractionCommandCatalog.CreateMvp();
        using var unknown = JsonDocument.Parse("""{"value":"x"}""");
        using var empty = JsonDocument.Parse("{}");

        var unknownException = NUnitCompat.Throws<InteractionCommandException>(
            () => catalog.Decode(
                "click",
                1,
                "menu.start",
                unknown.RootElement));
        var targetException = NUnitCompat.Throws<InteractionCommandException>(
            () => catalog.Decode(
                "click",
                1,
                " menu.start",
                empty.RootElement));

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                unknownException!.RejectionCode,
                Is.EqualTo(InteractionRejectionCode.InvalidArguments));
            Assert.That(
                targetException!.RejectionCode,
                Is.EqualTo(InteractionRejectionCode.InvalidArguments));
        });
    }

    [Test]
    public void UnknownCommandIsCommandNotRegistered()
    {
        using var document = JsonDocument.Parse("{}");
        var exception = NUnitCompat.Throws<InteractionCommandException>(
            () => InteractionCommandCatalog.CreateMvp().Decode(
                "unknown",
                1,
                "menu.start",
                document.RootElement));

        Assert.That(
            exception!.RejectionCode,
            Is.EqualTo(InteractionRejectionCode.CommandNotRegistered));
    }

    [Test]
    public void BuildRejectsIdentityAndClrTypeCollisions()
    {
        var identityCollision = new InteractionCommandCatalogBuilder()
            .Register("click", 1, ClickCommandSchema.Instance, true)
            .Register("click", 1, CustomCommandSchema.Instance, true);
        var typeCollision = new InteractionCommandCatalogBuilder()
            .Register("click", 1, ClickCommandSchema.Instance, true)
            .Register("click_v2", 2, ClickCommandSchema.Instance, true);

        NUnitCompat.Multiple(() =>
        {
            NUnitCompat.ThatThrows(
                () => identityCollision.Build(),
                Throws.TypeOf<InvalidOperationException>());
            NUnitCompat.ThatThrows(
                () => typeCollision.Build(),
                Throws.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public void BuildRejectsInvalidRegistrationAndSchema()
    {
        var emptyName = new InteractionCommandCatalogBuilder()
            .Register(string.Empty, 1, ClickCommandSchema.Instance, true);
        var invalidVersion = new InteractionCommandCatalogBuilder()
            .Register("click", 0, ClickCommandSchema.Instance, true);
        var nullSchema = new InteractionCommandCatalogBuilder()
            .Register<CustomCommand>("custom", 1, null!, true);
        var invalidSchema = new InteractionCommandCatalogBuilder()
            .Register(
                "custom",
                1,
                new CustomCommandSchema(
                    new InteractionArgumentSchema(
                        new[]
                        {
                            new InteractionArgumentDefinition(
                                string.Empty,
                                InteractionArgumentType.String,
                                true,
                                false),
                        })),
                true);
        var duplicateArguments = new InteractionCommandCatalogBuilder()
            .Register(
                "custom",
                1,
                new CustomCommandSchema(
                    new InteractionArgumentSchema(
                        new[]
                        {
                            new InteractionArgumentDefinition(
                                "value",
                                InteractionArgumentType.String,
                                true,
                                false),
                            new InteractionArgumentDefinition(
                                "value",
                                InteractionArgumentType.String,
                                false,
                                false),
                        })),
                true);

        NUnitCompat.Multiple(() =>
        {
            NUnitCompat.ThatThrows(() => emptyName.Build(), Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => invalidVersion.Build(),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            NUnitCompat.ThatThrows(() => nullSchema.Build(), Throws.ArgumentException);
            NUnitCompat.ThatThrows(() => invalidSchema.Build(), Throws.ArgumentException);
            NUnitCompat.ThatThrows(
                () => duplicateArguments.Build(),
                Throws.ArgumentException);
        });
    }

    [Test]
    public void CatalogIsImmutableSortedAndPreservesAgentVisibility()
    {
        var catalog = new InteractionCommandCatalogBuilder()
            .Register("set_value", 1, SetValueCommandSchema.Instance, false)
            .Register("click", 1, ClickCommandSchema.Instance, true)
            .Build();

        NUnitCompat.Multiple(() =>
        {
            Assert.That(
                catalog.Entries.Select(entry => entry.WireName),
                Is.EqualTo(new[] { "click", "set_value" }));
            Assert.That(catalog.Entries[0].AgentVisible, Is.True);
            Assert.That(catalog.Entries[1].AgentVisible, Is.False);
            Assert.That(catalog.Get<ClickCommand>(), Is.SameAs(catalog.Entries[0]));
        });
    }

    [Test]
    public async Task DecodedCommandUsesClosedGenericDispatcherDelegate()
    {
        var catalog = InteractionCommandCatalog.CreateMvp();
        using var document = JsonDocument.Parse("{}");
        var decoded = catalog.Decode(
            "click",
            1,
            "menu.start",
            document.RootElement);
        var dispatcher = new CapturingDispatcher();
        var options = new InteractionDispatchOptions(InteractionOrigin.Agent);

        var result = await decoded.DispatchAsync(dispatcher, options);

        NUnitCompat.Multiple(() =>
        {
            Assert.That(dispatcher.CommandType, Is.EqualTo(typeof(ClickCommand)));
            Assert.That(
                dispatcher.Command,
                Is.EqualTo(new ClickCommand("menu.start")));
            Assert.That(dispatcher.Options, Is.EqualTo(options));
            Assert.That(result.Status, Is.EqualTo(InteractionStatus.Rejected));
        });
    }

    [Test]
    public void SchemaCollectionsAreDefensivelyCopiedAndStructurallyEqual()
    {
        var source = new[]
        {
            new InteractionArgumentDefinition(
                "value",
                InteractionArgumentType.String,
                true,
                false),
        };
        var schema = new InteractionArgumentSchema(source);
        source[0] = new InteractionArgumentDefinition(
            "changed",
            InteractionArgumentType.Boolean,
            false,
            true);

        Assert.That(
            schema,
            Is.EqualTo(
                new InteractionArgumentSchema(
                    new[]
                    {
                        new InteractionArgumentDefinition(
                            "value",
                            InteractionArgumentType.String,
                            true,
                            false),
                    })));
    }

    private static string WriteArguments(DecodedInteractionCommand command)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            command.WriteArguments(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private readonly struct CustomCommand : IInteractionCommand
    {
        public CustomCommand(string targetId)
        {
            TargetId = targetId;
        }

        public string TargetId { get; }
    }

    private sealed class CustomCommandSchema :
        IInteractionCommandSchema<CustomCommand>
    {
        public CustomCommandSchema(InteractionArgumentSchema arguments)
        {
            Arguments = arguments;
        }

        public static CustomCommandSchema Instance { get; } =
            new CustomCommandSchema(InteractionArgumentSchema.Empty);

        public InteractionArgumentSchema Arguments { get; }

        public CustomCommand Decode(string targetId, JsonElement arguments)
        {
            return new CustomCommand(targetId);
        }

        public void WriteArguments(
            Utf8JsonWriter writer,
            in CustomCommand command)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }

    private sealed class CapturingDispatcher : IInteractionDispatcher
    {
        public Type? CommandType { get; private set; }

        public IInteractionCommand? Command { get; private set; }

        public InteractionDispatchOptions Options { get; private set; }

        public ValueTask<InteractionResult> DispatchAsync<TCommand>(
            TCommand command,
            InteractionDispatchOptions options,
            CancellationToken cancellationToken = default)
            where TCommand : struct, IInteractionCommand
        {
            CommandType = typeof(TCommand);
            Command = command;
            Options = options;
            return ValueTask.FromResult(
                new InteractionResult(
                    1,
                    "request-1",
                    command.TargetId,
                    "click",
                    1,
                    options.Origin,
                    InteractionStatus.Rejected,
                    new RejectionInfo(
                        InteractionRejectionCode.Disabled,
                        "Disabled for test."),
                    null,
                    StageProgress.Empty,
                    StateObservation.Empty,
                    StateObservation.Empty,
                    StateDiff.Empty));
        }
    }
}
