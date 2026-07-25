using System;
using System.Collections.Generic;
using SignalRouter.V2.Contracts;

namespace SignalRouter.V2.Kernel
{
    /// <summary>
    /// The multiple-producer, single-consumer mailbox (kernel-execution.md §4):
    /// three bounded classes — control, revision-bound source publication, mutation
    /// admission — with adoption as the single linearization point. Control overflow
    /// is a kernel fault (sized for the worst case, with the post-fence obligations
    /// of admitted work accounted separately so completions can never be starved by
    /// unrelated control traffic); mutation overflow refuses admission; publication
    /// overflow answers the publisher explicitly. Human-priority ordering applies at
    /// adoption, before `LogicalOrder` exists (kernel-execution.md §7).
    /// </summary>
    internal sealed class Mailbox
    {
        private readonly object gate = new object();
        private readonly Queue<ControlMessage> control = new Queue<ControlMessage>();
        private readonly Queue<ControlMessage> postFence = new Queue<ControlMessage>();
        private readonly Queue<SourcePublicationMessage> publications = new Queue<SourcePublicationMessage>();
        private readonly Queue<SubmissionMessage> humanSubmissions = new Queue<SubmissionMessage>();
        private readonly Queue<SubmissionMessage> submissions = new Queue<SubmissionMessage>();
        private readonly Dictionary<StateSourceKey, int> pendingPerSource = new Dictionary<StateSourceKey, int>();
        private readonly KernelOptions options;
        private int publicationBytes;

        internal Mailbox(KernelOptions options)
        {
            this.options = options;
        }

        internal void EnqueueControl(ControlMessage message)
        {
            lock (gate)
            {
                if (control.Count >= options.MailboxControlCapacity)
                {
                    throw new KernelFaultException(
                        "Control-lane overflow is a kernel fault; the class must be sized for the worst case.");
                }

                control.Enqueue(message);
            }
        }

        /// <summary>Fence/completion messages of admitted work: reserved accounting, never starved by control traffic.</summary>
        internal void EnqueuePostFence(ControlMessage message)
        {
            lock (gate)
            {
                if (postFence.Count >= options.MailboxMaxOutstandingPostFenceOperations)
                {
                    throw new KernelFaultException(
                        "Post-fence obligation overflow is a kernel fault; the bound tracks admitted work.");
                }

                postFence.Enqueue(message);
            }
        }

        internal AdapterSdk.PublicationAnswer EnqueuePublication(SourcePublicationMessage message)
        {
            lock (gate)
            {
                pendingPerSource.TryGetValue(message.Publication.Source, out var perSource);
                if (publications.Count >= options.SourcePublicationCapacity ||
                    publicationBytes + message.ApproximateBytes > options.SourcePublicationAggregateBytes ||
                    perSource >= options.SourcePublicationPendingPerSource)
                {
                    return AdapterSdk.PublicationAnswer.Refused;
                }

                publications.Enqueue(message);
                publicationBytes += message.ApproximateBytes;
                pendingPerSource[message.Publication.Source] = perSource + 1;
                return AdapterSdk.PublicationAnswer.Accepted;
            }
        }

        /// <summary>False = mutation-class overflow: the caller answers Rejected(CapacityExhausted).</summary>
        internal bool TryEnqueueSubmission(SubmissionMessage message)
        {
            lock (gate)
            {
                if (humanSubmissions.Count + submissions.Count >= options.MailboxMutationCapacity)
                {
                    return false;
                }

                if (message.HumanPriority)
                {
                    humanSubmissions.Enqueue(message);
                }
                else
                {
                    submissions.Enqueue(message);
                }

                return true;
            }
        }

        internal bool TryDequeueControl(out ControlMessage message)
        {
            lock (gate)
            {
                if (postFence.Count > 0)
                {
                    message = postFence.Dequeue();
                    return true;
                }

                if (control.Count > 0)
                {
                    message = control.Dequeue();
                    return true;
                }

                message = null!;
                return false;
            }
        }

        internal bool TryDequeuePublication(out SourcePublicationMessage message)
        {
            lock (gate)
            {
                if (publications.Count > 0)
                {
                    message = publications.Dequeue();
                    publicationBytes -= message.ApproximateBytes;
                    var count = pendingPerSource[message.Publication.Source] - 1;
                    if (count == 0)
                    {
                        pendingPerSource.Remove(message.Publication.Source);
                    }
                    else
                    {
                        pendingPerSource[message.Publication.Source] = count;
                    }

                    return true;
                }

                message = null!;
                return false;
            }
        }

        internal bool TryDequeueSubmission(out SubmissionMessage message)
        {
            lock (gate)
            {
                if (humanSubmissions.Count > 0)
                {
                    message = humanSubmissions.Dequeue();
                    return true;
                }

                if (submissions.Count > 0)
                {
                    message = submissions.Dequeue();
                    return true;
                }

                message = null!;
                return false;
            }
        }

        internal int ControlDepth
        {
            get
            {
                lock (gate)
                {
                    return control.Count + postFence.Count;
                }
            }
        }

        internal int PublicationDepth
        {
            get
            {
                lock (gate)
                {
                    return publications.Count;
                }
            }
        }

        internal int SubmissionDepth
        {
            get
            {
                lock (gate)
                {
                    return humanSubmissions.Count + submissions.Count;
                }
            }
        }
    }
}
