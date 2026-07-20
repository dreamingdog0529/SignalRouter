using System;

namespace SignalRouter
{
    // Time source for the recording session header (design §15). Core has no other
    // clock dependency; recordings order events by sequence, never by timestamps,
    // so the clock exists only to stamp startedAt.
    public interface IInteractionClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class InteractionSystemClock : IInteractionClock
    {
        private InteractionSystemClock()
        {
        }

        public static InteractionSystemClock Instance { get; } = new InteractionSystemClock();

        public DateTimeOffset UtcNow
        {
            get { return DateTimeOffset.UtcNow; }
        }
    }

    public enum InteractionRecordingError
    {
        MissingHeader = 0,
        UnsupportedSchemaVersion = 1,
        CorruptEntry = 2,
        NonMonotonicSequence = 3,
        UnmatchedTerminalEvent = 4,
        DuplicateTerminalEvent = 5,
        SizeLimitExceeded = 6,
        RecorderFailed = 7,
    }

    public sealed class InteractionRecordingException : Exception
    {
        public InteractionRecordingException(InteractionRecordingError error, string message)
            : this(error, message, null)
        {
        }

        public InteractionRecordingException(
            InteractionRecordingError error,
            string message,
            Exception? innerException)
            : base(ValidateMessage(message), innerException)
        {
            InteractionContract.RequireDefinedEnum(error, nameof(error));
            Error = error;
        }

        internal InteractionRecordingException(
            InteractionRecordingError error,
            string message,
            Exception? innerException,
            InteractionResult? completedResult)
            : this(error, message, innerException)
        {
            CompletedResult = completedResult;
        }

        public InteractionRecordingError Error { get; }

        // Set only when a terminal append failed after the interaction already
        // executed: the result is real (side effects happened) even though it could
        // not be persisted. The dispatcher uses it to keep the idempotency cache
        // truthful so a retry does not re-execute side effects.
        public InteractionResult? CompletedResult { get; }

        private static string ValidateMessage(string message)
        {
            InteractionContract.RequireMessage(message, nameof(message));
            return message;
        }
    }

    // Deterministic secret-key references written in place of sensitive argument
    // values (design §15.1): the recording carries {"$secret":"<name>@<version>/<arg>"}
    // and the replayer resolves the key from an in-memory store. The key is stable
    // across occurrences; a per-occurrence lookup can additionally use the enclosing
    // request event's requestId.
    public static class InteractionRecordingSecret
    {
        public const string PropertyName = "$secret";

        public static string KeyFor(string commandName, int commandVersion, string argumentName)
        {
            InteractionContract.RequireIdentifier(commandName, nameof(commandName));
            InteractionContract.RequireIdentifier(argumentName, nameof(argumentName));
            if (commandVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commandVersion),
                    commandVersion,
                    "Command versions must be positive.");
            }

            return commandName + "@"
                + commandVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "/" + argumentName;
        }
    }

    internal static class InteractionRecordingSchema
    {
        public const int SchemaVersion = 1;

        public const string KindProperty = "kind";
        public const string SessionKind = "session";
        public const string RequestedKind = "interaction_requested";
        public const string CompletedKind = "interaction_completed";

        public const string SchemaVersionProperty = "schemaVersion";
        public const string SessionIdProperty = "sessionId";
        public const string AppBuildProperty = "appBuild";
        public const string StartedAtProperty = "startedAt";

        public const string SequenceProperty = "sequence";
        public const string RequestIdProperty = "requestId";
        public const string OriginProperty = "origin";
        public const string CommandProperty = "command";
        public const string NameProperty = "name";
        public const string VersionProperty = "version";
        public const string TargetIdProperty = "targetId";
        public const string ArgumentsProperty = "arguments";

        public const string ResultProperty = "result";
        public const string StatusProperty = "status";
        public const string StagesProperty = "stages";
        public const string StageIdProperty = "id";
        public const string RejectionCodeProperty = "rejectionCode";
        public const string FaultCodeProperty = "faultCode";
        public const string StateProperty = "state";
        public const string BeforeProperty = "before";
        public const string AfterProperty = "after";
    }
}
