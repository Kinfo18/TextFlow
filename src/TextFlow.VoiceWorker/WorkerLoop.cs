using TextFlow.Contracts.Voice;

namespace TextFlow.VoiceWorker;

/// <summary>The worker side of the protocol: answers the hello, then serves messages until told to stop.</summary>
internal static class WorkerLoop
{
    public static async Task<int> RunAsync(Stream pipe, CancellationToken cancellationToken)
    {
        try
        {
            if (await VoiceFrame.ReadAsync(pipe, cancellationToken).ConfigureAwait(false) is not HelloMessage hello
                || hello.ProtocolVersion != VoiceProtocol.Version)
            {
                return WorkerExitCode.ProtocolError;
            }

            await VoiceFrame.WriteAsync(pipe, new ReadyMessage(VoiceProtocol.Version, Environment.ProcessId), cancellationToken)
                .ConfigureAwait(false);

            while (true)
            {
                switch (await VoiceFrame.ReadAsync(pipe, cancellationToken).ConfigureAwait(false))
                {
                    case null: // TextFlow closed the pipe
                    case ShutdownMessage:
                        return WorkerExitCode.Ok;
                    case PingMessage ping:
                        await VoiceFrame.WriteAsync(pipe, new PongMessage(ping.Id), cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        return WorkerExitCode.ProtocolError;
                }
            }
        }
        catch (InvalidDataException)
        {
            return WorkerExitCode.ProtocolError;
        }
        catch (IOException)
        {
            return WorkerExitCode.PipeBroken;
        }
        catch (OperationCanceledException)
        {
            return WorkerExitCode.ParentGone;
        }
    }
}
