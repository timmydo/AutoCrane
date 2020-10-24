﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using AutoCrane.Interfaces;
using AutoCrane.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoCrane.Apps
{
    public class WatchdogHealthz
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "ASP.NET")]
        public void ConfigureServices(IServiceCollection services)
        {
            DependencyInjection.Setup(new ConfigurationBuilder().AddEnvironmentVariables().Build(), services);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "ASP.NET")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2208:Instantiate argument exceptions correctly", Justification = "no")]
        public void Configure(IApplicationBuilder app)
        {
            app.UseMiddleware<LogRequestMiddleware>();
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/ping", (ctx) =>
                {
                    return ctx.Response.WriteAsync("ok");
                });

                endpoints.MapGet("/healthz", async (ctx) =>
                {
                    var healthMonitor = ctx.RequestServices.GetRequiredService<IConsecutiveHealthMonitor>();
                    var uptimeMonitor = ctx.RequestServices.GetRequiredService<IUptimeMonitor>();
                    var podOptions = ctx.RequestServices.GetRequiredService<IOptions<PodIdentifierOptions>>();
                    var healthOptions = ctx.RequestServices.GetRequiredService<IOptions<WatchdogHealthzOptions>>();
                    var lf = ctx.RequestServices.GetRequiredService<ILoggerFactory>();
                    var logger = lf.CreateLogger<WatchdogHealthz>();
                    var ns = podOptions.Value.Namespace ?? string.Empty;
                    var name = podOptions.Value.Name ?? string.Empty;
                    var alwaysHealthySeconds = healthOptions.Value.AlwaysHealthyAfterSeconds.GetValueOrDefault();
                    if (ns == string.Empty)
                    {
                        throw new ArgumentNullException(nameof(podOptions.Value.Namespace));
                    }

                    if (name == string.Empty)
                    {
                        throw new ArgumentNullException(nameof(podOptions.Value.Name));
                    }

                    if (!healthOptions.Value.AlwaysHealthyAfterSeconds.HasValue)
                    {
                        throw new ArgumentNullException(nameof(healthOptions.Value.AlwaysHealthyAfterSeconds));
                    }

                    var uptime = uptimeMonitor.Uptime;
                    if (uptime > TimeSpan.FromSeconds(alwaysHealthySeconds))
                    {
                        logger.LogInformation($"Uptime {uptime} surpassed {alwaysHealthySeconds}: success");
                        ctx.Response.StatusCode = 200;
                    }

                    var podid = new PodIdentifier(ns, name);
                    await healthMonitor.Probe(podid);

                    if (healthMonitor.IsHealthy(podid))
                    {
                        logger.LogInformation($"Pod {podid} is healthy");
                        ctx.Response.StatusCode = 200;
                        return;
                    }

                    logger.LogInformation($"Pod {podid} is not healthy or has not been for long enough");
                    ctx.Response.StatusCode = 500;
                });
            });
        }
    }
}