// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Listener;
using Microsoft.VisualStudio.Services.WebApi;
using Moq;
using Xunit;

using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Microsoft.VisualStudio.Services.Agent.Tests.Listener
{
    public sealed class JobDispatcherL0
    {
        private Mock<IProcessChannel> _processChannel;
        private Mock<IProcessInvoker> _processInvoker;
        private Mock<IAgentServer> _agentServer;
        private Mock<IConfigurationStore> _configurationStore;

        public JobDispatcherL0()
        {
            _processChannel = new Mock<IProcessChannel>();
            _processInvoker = new Mock<IProcessInvoker>();
            _agentServer = new Mock<IAgentServer>();
            _configurationStore = new Mock<IConfigurationStore>();
        }

        private Pipelines.AgentJobRequestMessage CreateJobRequestMessage()
        {
            TaskOrchestrationPlanReference plan = new TaskOrchestrationPlanReference();
            TimelineReference timeline = null;
            JobEnvironment environment = new JobEnvironment();
            List<TaskInstance> tasks = new List<TaskInstance>();
            Guid JobId = Guid.NewGuid();
            var jobRequest = new AgentJobRequestMessage(plan, timeline, JobId, "someJob", "someJob", environment, tasks);
            return Pipelines.AgentJobRequestMessageUtil.Convert(jobRequest);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        public async void DispatchesJobRequest()
        {
            //Arrange
            using (var hc = new TestHostContext(this))
            {
                var jobDispatcher = new JobDispatcher();
                hc.SetSingleton<IConfigurationStore>(_configurationStore.Object);
                hc.SetSingleton<IAgentServer>(_agentServer.Object);

                hc.EnqueueInstance<IProcessChannel>(_processChannel.Object);
                hc.EnqueueInstance<IProcessInvoker>(_processInvoker.Object);

                _configurationStore.Setup(x => x.GetSettings()).Returns(new AgentSettings() { PoolId = 1 });
                jobDispatcher.Initialize(hc);

                Pipelines.AgentJobRequestMessage message = CreateJobRequestMessage();
                string strMessage = JsonUtility.ToString(message);

                _processInvoker.Setup(x => x.ExecuteAsync(It.IsAny<String>(), It.IsAny<String>(), "spawnclient 1 2", null, It.IsAny<CancellationToken>()))
                    .Returns(Task.FromResult<int>(56));

                _processChannel.Setup(x => x.StartServer(It.IsAny<StartProcessDelegate>(), It.IsAny<bool>()))
                    .Callback((StartProcessDelegate startDel, bool disposeClient) => { startDel("1", "2"); });
                _processChannel.Setup(x => x.SendAsync(MessageType.NewJobRequest, It.Is<string>(s => s.Equals(strMessage)), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

                var request = new TaskAgentJobRequest();
                PropertyInfo sessionIdProperty = request.GetType().GetProperty("LockedUntil", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(sessionIdProperty);
                sessionIdProperty.SetValue(request, DateTime.UtcNow.AddMinutes(5));

                _agentServer.Setup(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.FromResult<TaskAgentJobRequest>(request));

                _agentServer.Setup(x => x.FinishAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<TaskResult>(), It.IsAny<CancellationToken>())).Returns(Task.FromResult<TaskAgentJobRequest>(new TaskAgentJobRequest()));

                //Actt
                jobDispatcher.Run(message);

                //Assert
                await jobDispatcher.WaitAsync(CancellationToken.None);

                Assert.False(jobDispatcher.RunOnceJobCompleted.Task.IsCompleted, "JobDispatcher should not set task complete token for regular agent.");
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA2000:Dispose objects before losing scope")]
        public async void DispatcherRenewJobRequest()
        {
            //Arrange
            using (var hc = new TestHostContext(this))
            {
                int poolId = 1;
                Int64 requestId = 1000;
                int count = 0;

                var trace = hc.GetTrace(nameof(DispatcherRenewJobRequest));
                TaskCompletionSource<int> firstJobRequestRenewed = new TaskCompletionSource<int>();
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

                TaskAgentJobRequest request = new TaskAgentJobRequest();
                PropertyInfo lockUntilProperty = request.GetType().GetProperty("LockedUntil", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(lockUntilProperty);
                lockUntilProperty.SetValue(request, DateTime.UtcNow.AddMinutes(5));

                hc.SetSingleton<IAgentServer>(_agentServer.Object);
                hc.SetSingleton<IConfigurationStore>(_configurationStore.Object);
                _configurationStore.Setup(x => x.GetSettings()).Returns(new AgentSettings() { PoolId = 1 });
                _agentServer.Setup(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                            .Returns(() =>
                            {
                                count++;
                                if (!firstJobRequestRenewed.Task.IsCompletedSuccessfully)
                                {
                                    trace.Info("First renew happens.");
                                }

                                if (count < 5)
                                {
                                    return Task.FromResult<TaskAgentJobRequest>(request);
                                }
                                else if (count == 5)
                                {
                                    cancellationTokenSource.Cancel();
                                    return Task.FromResult<TaskAgentJobRequest>(request);
                                }
                                else
                                {
                                    throw new InvalidOperationException("Should not reach here.");
                                }
                            });

                var jobDispatcher = new JobDispatcher();
                jobDispatcher.Initialize(hc);

                await jobDispatcher.RenewJobRequestAsync(poolId, requestId, Guid.Empty, firstJobRequestRenewed, cancellationTokenSource.Token);

                Assert.True(firstJobRequestRenewed.Task.IsCompletedSuccessfully);
                _agentServer.Verify(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA2000:Dispose objects before losing scope")]
        public async void DispatcherRenewJobRequestStopOnJobNotFoundExceptions()
        {
            //Arrange
            using (var hc = new TestHostContext(this))
            {
                int poolId = 1;
                Int64 requestId = 1000;
                int count = 0;

                var trace = hc.GetTrace(nameof(DispatcherRenewJobRequestStopOnJobNotFoundExceptions));
                TaskCompletionSource<int> firstJobRequestRenewed = new TaskCompletionSource<int>();
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

                TaskAgentJobRequest request = new TaskAgentJobRequest();
                PropertyInfo lockUntilProperty = request.GetType().GetProperty("LockedUntil", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(lockUntilProperty);
                lockUntilProperty.SetValue(request, DateTime.UtcNow.AddMinutes(5));

                hc.SetSingleton<IAgentServer>(_agentServer.Object);
                hc.SetSingleton<IConfigurationStore>(_configurationStore.Object);
                _configurationStore.Setup(x => x.GetSettings()).Returns(new AgentSettings() { PoolId = 1 });
                _agentServer.Setup(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                            .Returns(() =>
                            {
                                count++;
                                if (!firstJobRequestRenewed.Task.IsCompletedSuccessfully)
                                {
                                    trace.Info("First renew happens.");
                                }

                                if (count < 5)
                                {
                                    return Task.FromResult<TaskAgentJobRequest>(request);
                                }
                                else if (count == 5)
                                {
                                    cancellationTokenSource.CancelAfter(10000);
                                    throw new TaskAgentJobNotFoundException("");
                                }
                                else
                                {
                                    throw new InvalidOperationException("Should not reach here.");
                                }
                            });

                var jobDispatcher = new JobDispatcher();
                jobDispatcher.Initialize(hc);

                await jobDispatcher.RenewJobRequestAsync(poolId, requestId, Guid.Empty, firstJobRequestRenewed, cancellationTokenSource.Token);

                Assert.True(firstJobRequestRenewed.Task.IsCompletedSuccessfully, "First renew should succeed.");
                Assert.False(cancellationTokenSource.IsCancellationRequested);
                _agentServer.Verify(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA2000:Dispose objects before losing scope")]
        public async void DispatcherRenewJobRequestStopOnJobTokenExpiredExceptions()
        {
            //Arrange
            using (var hc = new TestHostContext(this))
            {
                int poolId = 1;
                Int64 requestId = 1000;
                int count = 0;

                var trace = hc.GetTrace(nameof(DispatcherRenewJobRequestStopOnJobTokenExpiredExceptions));
                TaskCompletionSource<int> firstJobRequestRenewed = new TaskCompletionSource<int>();
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

                TaskAgentJobRequest request = new TaskAgentJobRequest();
                PropertyInfo lockUntilProperty = request.GetType().GetProperty("LockedUntil", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(lockUntilProperty);
                lockUntilProperty.SetValue(request, DateTime.UtcNow.AddMinutes(5));

                hc.SetSingleton<IAgentServer>(_agentServer.Object);
                hc.SetSingleton<IConfigurationStore>(_configurationStore.Object);
                _configurationStore.Setup(x => x.GetSettings()).Returns(new AgentSettings() { PoolId = 1 });
                _agentServer.Setup(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                            .Returns(() =>
                            {
                                count++;
                                if (!firstJobRequestRenewed.Task.IsCompletedSuccessfully)
                                {
                                    trace.Info("First renew happens.");
                                }

                                if (count < 5)
                                {
                                    return Task.FromResult<TaskAgentJobRequest>(request);
                                }
                                else if (count == 5)
                                {
                                    cancellationTokenSource.CancelAfter(10000);
                                    throw new TaskAgentJobTokenExpiredException("");
                                }
                                else
                                {
                                    throw new InvalidOperationException("Should not reach here.");
                                }
                            });

                var jobDispatcher = new JobDispatcher();
                jobDispatcher.Initialize(hc);

                await jobDispatcher.RenewJobRequestAsync(poolId, requestId, Guid.Empty, firstJobRequestRenewed, cancellationTokenSource.Token);

                Assert.True(firstJobRequestRenewed.Task.IsCompletedSuccessfully, "First renew should succeed.");
                Assert.False(cancellationTokenSource.IsCancellationRequested);
                _agentServer.Verify(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA2000:Dispose objects before losing scope")]
        public async void DispatcherRenewJobRequestRecoverFromExceptions()
        {
            //Arrange
            using (var hc = new TestHostContext(this))
            {
                int poolId = 1;
                Int64 requestId = 1000;
                int count = 0;

                var trace = hc.GetTrace(nameof(DispatcherRenewJobRequestRecoverFromExceptions));
                TaskCompletionSource<int> firstJobRequestRenewed = new TaskCompletionSource<int>();
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

                TaskAgentJobRequest request = new TaskAgentJobRequest();
                PropertyInfo lockUntilProperty = request.GetType().GetProperty("LockedUntil", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(lockUntilProperty);
                lockUntilProperty.SetValue(request, DateTime.UtcNow.AddMinutes(5));

                hc.SetSingleton<IAgentServer>(_agentServer.Object);
                hc.SetSingleton<IConfigurationStore>(_configurationStore.Object);
                _configurationStore.Setup(x => x.GetSettings()).Returns(new AgentSettings() { PoolId = 1 });
                _agentServer.Setup(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                            .Returns(() =>
                            {
                                count++;
                                if (!firstJobRequestRenewed.Task.IsCompletedSuccessfully)
                                {
                                    trace.Info("First renew happens.");
                                }

                                if (count < 5)
                                {
                                    return Task.FromResult<TaskAgentJobRequest>(request);
                                }
                                else if (count == 5 || count == 6 || count == 7)
                                {
                                    throw new TimeoutException("");
                                }
                                else
                                {
                                    cancellationTokenSource.Cancel();
                                    return Task.FromResult<TaskAgentJobRequest>(request);
                                }
                            });

                var jobDispatcher = new JobDispatcher();
                jobDispatcher.Initialize(hc);

                await jobDispatcher.RenewJobRequestAsync(poolId, requestId, Guid.Empty, firstJobRequestRenewed, cancellationTokenSource.Token);

                Assert.True(firstJobRequestRenewed.Task.IsCompletedSuccessfully, "First renew should succeed.");
                Assert.True(cancellationTokenSource.IsCancellationRequested);
                _agentServer.Verify(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(8));
                _agentServer.Verify(x => x.RefreshConnectionAsync(AgentConnectionType.JobRequest, It.IsAny<TimeSpan>()), Times.Exactly(3));
                _agentServer.Verify(x => x.SetConnectionTimeout(AgentConnectionType.JobRequest, It.IsAny<TimeSpan>()), Times.Once);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA2000:Dispose objects before losing scope")]
        public async void DispatcherRenewJobRequestFirstRenewRetrySixTimes()
        {
            //Arrange
            using (var hc = new TestHostContext(this))
            {
                int poolId = 1;
                Int64 requestId = 1000;
                int count = 0;

                var trace = hc.GetTrace(nameof(DispatcherRenewJobRequestFirstRenewRetrySixTimes));
                TaskCompletionSource<int> firstJobRequestRenewed = new TaskCompletionSource<int>();
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

                TaskAgentJobRequest request = new TaskAgentJobRequest();
                PropertyInfo lockUntilProperty = request.GetType().GetProperty("LockedUntil", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(lockUntilProperty);
                lockUntilProperty.SetValue(request, DateTime.UtcNow.AddMinutes(5));

                hc.SetSingleton<IAgentServer>(_agentServer.Object);
                hc.SetSingleton<IConfigurationStore>(_configurationStore.Object);
                _configurationStore.Setup(x => x.GetSettings()).Returns(new AgentSettings() { PoolId = 1 });
                _agentServer.Setup(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                            .Returns(() =>
                            {
                                count++;
                                if (!firstJobRequestRenewed.Task.IsCompletedSuccessfully)
                                {
                                    trace.Info("First renew happens.");
                                }

                                if (count <= 5)
                                {
                                    throw new TimeoutException("");
                                }
                                else
                                {
                                    cancellationTokenSource.CancelAfter(10000);
                                    throw new InvalidOperationException("Should not reach here.");
                                }
                            });

                var jobDispatcher = new JobDispatcher();
                jobDispatcher.Initialize(hc);

                await jobDispatcher.RenewJobRequestAsync(poolId, requestId, Guid.Empty, firstJobRequestRenewed, cancellationTokenSource.Token);

                Assert.False(firstJobRequestRenewed.Task.IsCompletedSuccessfully, "First renew should failed.");
                Assert.False(cancellationTokenSource.IsCancellationRequested);
                _agentServer.Verify(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(6));
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA2000:Dispose objects before losing scope")]
        public async void DispatcherRenewJobRequestStopOnExpiredRequest()
        {
            //Arrange
            using (var hc = new TestHostContext(this))
            {
                int poolId = 1;
                Int64 requestId = 1000;
                int count = 0;

                var trace = hc.GetTrace(nameof(DispatcherRenewJobRequestStopOnExpiredRequest));
                TaskCompletionSource<int> firstJobRequestRenewed = new TaskCompletionSource<int>();
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

                TaskAgentJobRequest request = new TaskAgentJobRequest();
                PropertyInfo lockUntilProperty = request.GetType().GetProperty("LockedUntil", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(lockUntilProperty);
                lockUntilProperty.SetValue(request, DateTime.UtcNow.AddMinutes(5));

                hc.SetSingleton<IAgentServer>(_agentServer.Object);
                hc.SetSingleton<IConfigurationStore>(_configurationStore.Object);
                _configurationStore.Setup(x => x.GetSettings()).Returns(new AgentSettings() { PoolId = 1 });
                _agentServer.Setup(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                            .Returns(() =>
                            {
                                count++;
                                if (!firstJobRequestRenewed.Task.IsCompletedSuccessfully)
                                {
                                    trace.Info("First renew happens.");
                                }

                                if (count == 1)
                                {
                                    return Task.FromResult<TaskAgentJobRequest>(request);
                                }
                                else if (count < 5)
                                {
                                    throw new TimeoutException("");
                                }
                                else if (count == 5)
                                {
                                    lockUntilProperty.SetValue(request, DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(11)));
                                    throw new TimeoutException("");
                                }
                                else
                                {
                                    cancellationTokenSource.CancelAfter(10000);
                                    throw new InvalidOperationException("Should not reach here.");
                                }
                            });

                var jobDispatcher = new JobDispatcher();
                jobDispatcher.Initialize(hc);

                await jobDispatcher.RenewJobRequestAsync(poolId, requestId, Guid.Empty, firstJobRequestRenewed, cancellationTokenSource.Token);

                Assert.True(firstJobRequestRenewed.Task.IsCompletedSuccessfully, "First renew should succeed.");
                Assert.False(cancellationTokenSource.IsCancellationRequested);
                _agentServer.Verify(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
                _agentServer.Verify(x => x.RefreshConnectionAsync(AgentConnectionType.JobRequest, It.IsAny<TimeSpan>()), Times.Exactly(3));
                _agentServer.Verify(x => x.SetConnectionTimeout(AgentConnectionType.JobRequest, It.IsAny<TimeSpan>()), Times.Never);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        public async void DispatchesOneTimeJobRequest()
        {
            //Arrange
            using (var hc = new TestHostContext(this))
            {
                var jobDispatcher = new JobDispatcher();
                hc.SetSingleton<IConfigurationStore>(_configurationStore.Object);
                hc.SetSingleton<IAgentServer>(_agentServer.Object);

                hc.EnqueueInstance<IProcessChannel>(_processChannel.Object);
                hc.EnqueueInstance<IProcessInvoker>(_processInvoker.Object);

                _configurationStore.Setup(x => x.GetSettings()).Returns(new AgentSettings() { PoolId = 1 });
                jobDispatcher.Initialize(hc);

                Pipelines.AgentJobRequestMessage message = CreateJobRequestMessage();
                string strMessage = JsonUtility.ToString(message);

                _processInvoker.Setup(x => x.ExecuteAsync(It.IsAny<String>(), It.IsAny<String>(), "spawnclient 1 2", null, It.IsAny<CancellationToken>()))
                    .Returns(Task.FromResult<int>(56));

                _processChannel.Setup(x => x.StartServer(It.IsAny<StartProcessDelegate>(), It.IsAny<bool>()))
                    .Callback((StartProcessDelegate startDel, bool disposeClient) => { startDel("1", "2"); });
                _processChannel.Setup(x => x.SendAsync(MessageType.NewJobRequest, It.Is<string>(s => s.Equals(strMessage)), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

                var request = new TaskAgentJobRequest();
                PropertyInfo sessionIdProperty = request.GetType().GetProperty("LockedUntil", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.NotNull(sessionIdProperty);
                sessionIdProperty.SetValue(request, DateTime.UtcNow.AddMinutes(5));

                _agentServer.Setup(x => x.RenewAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.FromResult<TaskAgentJobRequest>(request));

                _agentServer.Setup(x => x.FinishAgentRequestAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<TaskResult>(), It.IsAny<CancellationToken>())).Returns(Task.FromResult<TaskAgentJobRequest>(new TaskAgentJobRequest()));

                //Act
                jobDispatcher.Run(message, true);

                //Assert
                await jobDispatcher.WaitAsync(CancellationToken.None);

                Assert.True(jobDispatcher.RunOnceJobCompleted.Task.IsCompleted, "JobDispatcher should set task complete token for one time agent.");
                Assert.True(jobDispatcher.RunOnceJobCompleted.Task.Result, "JobDispatcher should set task complete token to 'TRUE' for one time agent.");
            }
        }
    }
}
