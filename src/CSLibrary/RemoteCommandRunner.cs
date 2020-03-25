using System;
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
                   command: new []{"/bin/sh"}, container: containerName,
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
    }
}
