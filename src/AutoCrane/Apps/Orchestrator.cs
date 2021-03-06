﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoCrane.Interfaces;
using AutoCrane.Models;
using Microsoft.Extensions.Logging;

namespace AutoCrane.Apps
{
    public sealed class Orchestrator : IAutoCraneService
    {
        private const int ConsecutiveErrorCountBeforeExiting = 5;
        private const int IterationLoopSeconds = 60;
        private const int WatchdogFailuresBeforeEviction = 3;
        private readonly IAutoCraneConfig config;
        private readonly IFailingPodGetter failingPodGetter;
        private readonly IPodEvicter podEvicter;
        private readonly IPodDataRequestGetter dataRequestGetter;
        private readonly IDataRepositoryManifestFetcher manifestFetcher;
        private readonly IPodAnnotationPutter podAnnotationPutter;
        private readonly IDataRepositoryKnownGoodAccessor knownGoodAccessor;
        private readonly IDataRepositoryLatestVersionAccessor upgradeAccessor;
        private readonly IDataRepositoryUpgradeOracleFactory upgradeOracleFactory;
        private readonly IClock clock;
        private readonly ILeaderElection leaderElection;
        private readonly IExpiredObjectDeleter expiredObjectDeleter;
        private readonly ILogger<Orchestrator> logger;

        public Orchestrator(IAutoCraneConfig config, ILoggerFactory loggerFactory, IFailingPodGetter failingPodGetter, IPodEvicter podEvicter, IPodDataRequestGetter podGetter, IDataRepositoryManifestFetcher manifestFetcher, IPodAnnotationPutter podAnnotationPutter, IDataRepositoryKnownGoodAccessor knownGoodAccessor, IDataRepositoryLatestVersionAccessor upgradeAccessor, IDataRepositoryUpgradeOracleFactory upgradeOracleFactory, IClock clock, ILeaderElection leaderElection, IExpiredObjectDeleter expiredObjectDeleter)
        {
            this.config = config;
            this.failingPodGetter = failingPodGetter;
            this.podEvicter = podEvicter;
            this.dataRequestGetter = podGetter;
            this.manifestFetcher = manifestFetcher;
            this.podAnnotationPutter = podAnnotationPutter;
            this.knownGoodAccessor = knownGoodAccessor;
            this.upgradeAccessor = upgradeAccessor;
            this.upgradeOracleFactory = upgradeOracleFactory;
            this.clock = clock;
            this.leaderElection = leaderElection;
            this.expiredObjectDeleter = expiredObjectDeleter;
            this.logger = loggerFactory.CreateLogger<Orchestrator>();
        }

        public async Task<int> RunAsync(CancellationToken token)
        {
            var iterations = int.MaxValue;
            var errorCount = 0;
            if (!this.config.Namespaces.Any())
            {
                this.logger.LogError($"No namespaces configured to watch... set env var AutoCrane__Namespaces to a comma-separated value");
                return 3;
            }

            var backgroundTask = this.leaderElection.StartBackgroundTask("acleaderorchestrate", TimeSpan.FromSeconds(30), token);

            var podsWithFailingWatchdog = new Queue<List<PodIdentifier>>();

            while (iterations > 0)
            {
                if (errorCount > ConsecutiveErrorCountBeforeExiting)
                {
                    this.logger.LogError($"Hit max consecutive error count...exiting...");
                    return 2;
                }

                if (backgroundTask.IsCompleted)
                {
                    this.logger.LogError($"Leadership election task has completed");
                    return 3;
                }

                try
                {
                    token.ThrowIfCancellationRequested();

                    if (this.leaderElection.IsLeader)
                    {
                        await this.ProcessFailingPodsAndDataRequestsAsync(podsWithFailingWatchdog, token);
                        await this.CleanupExpiredItems(token);
                    }
                    else
                    {
                        // not the leader
                        this.logger.LogInformation("Not the leader");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(IterationLoopSeconds), token);
                    iterations--;
                    errorCount = 0;
                }
                catch (Exception e)
                {
                    this.logger.LogError($"Unhandled exception: {e}");
                    errorCount++;
                    await Task.Delay(TimeSpan.FromSeconds(IterationLoopSeconds), token);
                }
            }

            return 0;
        }

        private async Task CleanupExpiredItems(CancellationToken token)
        {
            foreach (var ns in this.config.Namespaces)
            {
                await this.expiredObjectDeleter.DeleteExpiredObjectsAsync(ns, this.clock.Get(), token);
            }
        }

        private async Task ProcessFailingPodsAndDataRequestsAsync(Queue<List<PodIdentifier>> podsWithFailingWatchdog, CancellationToken token)
        {
            var manifest = await this.manifestFetcher.FetchAsync(token);

            var thisIterationFailingPods = new List<PodIdentifier>();
            foreach (var ns in this.config.Namespaces)
            {
                // this code gets the LKG versions, the latest versions, and all the existing pods, then creates an oracle to decide what to update (if any)
                var requests = await this.dataRequestGetter.GetAsync(ns);
                var knownGoodVersions = await this.knownGoodAccessor.GetOrUpdateAsync(ns, manifest, requests, token);
                var latestVersions = await this.upgradeAccessor.GetOrUpdateAsync(ns, manifest, token);
                var oracle = this.upgradeOracleFactory.Create(knownGoodVersions, latestVersions, requests);

                // this updates the data request annotations based on what the oracle says. then the thing getting the data will pull the latest
                await this.ProcessDataRequestsAsync(oracle, requests);

                var failingPods = await this.failingPodGetter.GetFailingPodsAsync(ns);
                thisIterationFailingPods.AddRange(failingPods);
            }

            while (podsWithFailingWatchdog.Count > WatchdogFailuresBeforeEviction)
            {
                podsWithFailingWatchdog.Dequeue();
            }

            if (podsWithFailingWatchdog.Count == WatchdogFailuresBeforeEviction)
            {
                var podsFailingEveryWatchdog = new HashSet<PodIdentifier>(thisIterationFailingPods);
                foreach (var iteration in podsWithFailingWatchdog)
                {
                    podsFailingEveryWatchdog.IntersectWith(iteration);
                }

                if (podsFailingEveryWatchdog.Any())
                {
                    await this.EvictPods(podsFailingEveryWatchdog);
                }
            }

            podsWithFailingWatchdog.Enqueue(thisIterationFailingPods);
        }

        private async Task ProcessDataRequestsAsync(IDataRepositoryUpgradeOracle oracle, IReadOnlyList<PodDataRequestInfo> podRequests)
        {
            foreach (var podRequest in podRequests)
            {
                var annotationsToAdd = new List<KeyValuePair<string, string>>();
                foreach (var repoName in podRequest.DataSources)
                {
                    var newRequest = oracle.GetDataRequest(podRequest.Id, repoName);
                    if (newRequest != null)
                    {
                        newRequest.UnixTimestampSeconds = this.clock.Get().ToUnixTimeSeconds();
                        this.logger.LogInformation($"Pod {podRequest.Id} to request data {repoName}, request = '{newRequest}'");
                        annotationsToAdd.Add(new KeyValuePair<string, string>($"{CommonAnnotations.DataRequestPrefix}{repoName}", newRequest.ToBase64String()));
                    }
                    else
                    {
                        this.logger.LogTrace($"Pod {podRequest.Id} no update for data {repoName}.");
                    }
                }

                if (annotationsToAdd.Any())
                {
                    await this.podAnnotationPutter.PutPodAnnotationAsync(podRequest.Id, annotationsToAdd);
                }
            }
        }

        private Task EvictPods(HashSet<PodIdentifier> pods)
        {
            return Task.WhenAll(pods.Select(p => this.podEvicter.EvictPodAsync(p)).ToArray());
        }
    }
}
