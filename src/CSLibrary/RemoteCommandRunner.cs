using System;
using System.IO;
using System.Threading.Tasks;
using k8s;
using k8s.Models;
using Microsoft.Rest.Serialization;

namespace CSLibrary
{
    // This is a helper command for talking to k8s without mucking with the
    // F# async system, which appears even harder than the mainstream dotnet
    // tasking system to operate without deadlocks.
    public class RemoteCommandRunner
    {
        public static int RunRemoteCommand(Kubernetes kube, string ns, string podName,
            string containerName, string shellCmdStrIncludingNewLine)
        {
            if (shellCmdStrIncludingNewLine.Length >= 4096)
            {
                // There appears to be a bug / feature that makes a deadlock if we run this
                // code with more data sent than about 4096 bytes. This appears to be some
                // kind of buffering issue but in any case you should stay below!
                //
                // Possibly this is happening on the server, for example this bug suggests
                // the ingress we're using (traefik) might truncate websocket messages
                // at 4096 bytes: https://github.com/containous/traefik/issues/5083
                throw new ArgumentException("passing shell command >= 4096 bytes");
            }

            if (!shellCmdStrIncludingNewLine.EndsWith("\n"))
            {
                throw new ArgumentException("shell command must end in newline");
            }

            if (!shellCmdStrIncludingNewLine.Contains("exit"))
            {
                throw new ArgumentException("shell command must call exit");
            }

            Task<int> task = RunRemoteCommandAsync(kube, ns, podName, containerName,
                shellCmdStrIncludingNewLine);
            return task.Result;
        }

        public static async Task<int> RunRemoteCommandAsync(Kubernetes kube, string ns, string podName,
            string containerName, string shellCmdStrIncludingNewLine)
        {
            using (var mstr =
               await kube.MuxedStreamNamespacedPodExecAsync(name: podName, @namespace: ns,
                   // We specifically use /bin/bash here to avoid hitting Almquist
                   // /bin/sh shells popular in several container distros that have
                   // an even smaller input limit -- 1024 bytes!
                   command: new []{"/bin/bash"}, container: containerName,
                   stdin: true).ConfigureAwait(false))
            using (System.IO.Stream stdIn = mstr.GetStream(null, ChannelIndex.StdIn))
            using (System.IO.Stream error = mstr.GetStream(ChannelIndex.Error, null))
            using (System.IO.StreamWriter stdinWriter = new System.IO.StreamWriter(stdIn))
            using (System.IO.StreamReader errorReader = new System.IO.StreamReader(error))
            {
               mstr.Start();
               await stdinWriter.WriteAsync(shellCmdStrIncludingNewLine).ConfigureAwait(false);
               await stdinWriter.FlushAsync().ConfigureAwait(false);
               var errors = await errorReader.ReadToEndAsync().ConfigureAwait(false);
               return Kubernetes.GetExitCodeOrThrow(SafeJsonConvert.DeserializeObject<V1Status>(errors));
            }
        }

        // Execute a command and capture stdout to a file (for copying files from pod)
        // Returns the command's exit code
        public static int RunRemoteCommandAndCaptureOutput(Kubernetes kube, string ns, string podName,
            string containerName, string[] command, string outputFilePath)
        {
            Task<int> task = RunRemoteCommandAndCaptureOutputAsync(kube, ns, podName, containerName, command, outputFilePath);
            return task.Result;
        }

        public static async Task<int> RunRemoteCommandAndCaptureOutputAsync(Kubernetes kube, string ns, string podName,
            string containerName, string[] command, string outputFilePath)
        {
            // The `using` lifetime guard ensure these objects lifetimes last the entire task,
            // and is properly disposed after the task finishes.
            using (var mstr =
               await kube.MuxedStreamNamespacedPodExecAsync(
                   name: podName,
                   @namespace: ns,
                   command: command,
                   container: containerName,
                   stdin: false,
                   stdout: true,
                   stderr: true,
                   tty: false).ConfigureAwait(false))
            using (System.IO.Stream stdout = mstr.GetStream(ChannelIndex.StdOut, null))
            using (System.IO.Stream statusChannel = mstr.GetStream(ChannelIndex.Error, null))
            using (System.IO.StreamReader statusReader = new System.IO.StreamReader(statusChannel))
            using (System.IO.FileStream fileStream = new System.IO.FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None,
        bufferSize: 128 * 1024, useAsync: true))
            {
                // Start the MuxStream, this establishes the connection and routes bytes back into separate channels.
                // We only care about stdout(1) and the status channel(3).
                mstr.Start();

                // Copy stdout → file asynchronously, and drain the status channel concurrently.
                var copyTask = stdout.CopyToAsync(fileStream);
                var statusTask = statusReader.ReadToEndAsync();

                await Task.WhenAll(copyTask, statusTask).ConfigureAwait(false);

                // Flush the file stream to ensure all data is written
                await fileStream.FlushAsync().ConfigureAwait(false);

                string status = statusTask.Result;
                return Kubernetes.GetExitCodeOrThrow(SafeJsonConvert.DeserializeObject<V1Status>(status));
            }
        }
    }
}
