﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AutoCrane.Interfaces;
using AutoCrane.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoCrane.Services
{
    internal sealed class DataDownloadRequestFactory : IDataDownloadRequestFactory
    {
        private readonly ILogger<DataDownloadRequestFactory> logger;
        private readonly IOptions<PodIdentifierOptions> thisPodOptions;
        private readonly IPodDataRequestGetter podDataRequestGetter;

        public DataDownloadRequestFactory(ILoggerFactory loggerFactory, IOptions<PodIdentifierOptions> thisPodOptions, IPodDataRequestGetter podDataRequestGetter)
        {
            this.logger = loggerFactory.CreateLogger<DataDownloadRequestFactory>();
            this.thisPodOptions = thisPodOptions;
            this.podDataRequestGetter = podDataRequestGetter;
        }

        public Task<IList<DataDownloadRequest>> GetPodRequestsAsync()
        {
            return this.GetPodRequestsAsync(this.thisPodOptions.Value.Identifier);
        }

        public async Task<IList<DataDownloadRequest>> GetPodRequestsAsync(PodIdentifier pod)
        {
            this.logger.LogInformation($"Getting pod info {pod}");
            var podInfo = await this.podDataRequestGetter.GetAsync(pod);
            var list = new List<DataDownloadRequest>();

            if (string.IsNullOrEmpty(podInfo.DropFolder))
            {
                this.logger.LogError($"{CommonAnnotations.DataStoreLocation} is not set, returning empty list of pod data requests");
                return list;
            }

            foreach (var repo in podInfo.DataSources)
            {
                if (podInfo.Requests.TryGetValue(repo, out var request))
                {
                    var details = DataDownloadRequestDetails.FromBase64Json(request);
                    if (details is null || details.Hash is null || details.Path is null)
                    {
                        this.logger.LogError($"Cannot parse pod {podInfo.Id} DataDownloadRequestDetails {request}");
                        continue;
                    }

                    var extractionLocation = Path.Combine(podInfo.DropFolder, details.Path.Replace(Path.PathSeparator, '_'));
                    list.Add(new DataDownloadRequest(pod, repo, podInfo.DropFolder, extractionLocation, details));
                }
                else
                {
                    list.Add(new DataDownloadRequest(pod, repo, podInfo.DropFolder, string.Empty, null));
                }
            }

            return list;
        }
    }
}
