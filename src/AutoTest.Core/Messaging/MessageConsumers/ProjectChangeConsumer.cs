﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutoTest.Core.Messaging;
using AutoTest.Core.BuildRunners;
using AutoTest.Core.Configuration;
using AutoTest.Core.Caching;
using AutoTest.Core.Caching.Projects;
using AutoTest.Core.TestRunners;
using System.IO;
using AutoTest.Core.TestRunners.TestRunners;
using Castle.Core.Logging;
using AutoTest.Core.DebugLog;

namespace AutoTest.Core.Messaging.MessageConsumers
{
    class ProjectChangeConsumer : IBlockingConsumerOf<ProjectChangeMessage>
    {
        private IMessageBus _bus;
        private ICache _cache;
        private IConfiguration _configuration;
        private IBuildRunner _buildRunner;
        private ITestRunner[] _testRunners;

        public ProjectChangeConsumer(IMessageBus bus, ICache cache, IConfiguration configuration, IBuildRunner buildRunner, ITestRunner[] testRunners)
        {
            _bus = bus;
            _cache = cache;
            _configuration = configuration;
            _buildRunner = buildRunner;
            _testRunners = testRunners;
        }

        #region IConsumerOf<ProjectChangeMessage> Members

        public void Consume(ProjectChangeMessage message)
        {
            Debug.ConsumingProjectChangeMessage(message);
            _bus.Publish(new RunStartedMessage(message.Files));
            var alreadyBuilt = new List<string>();
            var runReport = new RunReport();
            foreach (var file in message.Files)
            {
                var project = _cache.Get<Project>(file.FullName);
                // Prioritized tests that test me
                // Other prioritized tests
                // Projects that tests me
                // Other test projects
                buildAndRunTests(project, runReport, alreadyBuilt);
            }
            _bus.Publish(new RunFinishedMessage(runReport));
        }

        private bool buildAndRunTests(Project project, RunReport runReport, List<string> alreadyBuilt)
        {
            if (alreadyBuilt.Contains(project.Key))
                return true;

            alreadyBuilt.Add(project.Key);

            if (File.Exists(_configuration.BuildExecutable(project.Value)))
            {
                _bus.Publish(new RunInformationMessage(
                                 InformationType.Build,
                                 project.Key,
                                 project.Value.AssemblyName,
                                 typeof(MSBuildRunner)));
                if (!buildProject(project))
                {
                    runReport.NumberOfBuildsFailed++;
                    return false;
                }
                runReport.NumberOfBuildsSucceeded++;
            }

            if (project.Value.ContainsTests)
                runTests(project, runReport);

            foreach (var reference in project.Value.ReferencedBy)
            {
                if (!buildAndRunTests(_cache.Get<Project>(reference), runReport, alreadyBuilt))
                    return false;
            }

            return true;
        }

        private bool buildProject(Project project)
        {
            var buildReport = _buildRunner.RunBuild(project.Key, _configuration.BuildExecutable(project.Value));
            _bus.Publish(new BuildRunMessage(buildReport));
            return buildReport.ErrorCount == 0;
        }

        private void runTests(Project project, RunReport runReport)
        {
            string folder = Path.Combine(Path.GetDirectoryName(project.Key), project.Value.OutputPath);
            var file = Path.Combine(folder, project.Value.AssemblyName);
            foreach (var runner in _testRunners)
            {
                if (runner.CanHandleTestFor(project.Value))
                    runTests(runner, project, file, runReport);
            }
        }

        #endregion

        private void runTests(ITestRunner testRunner, Project project, string assembly, RunReport runReport)
        {
            _bus.Publish(new RunInformationMessage(InformationType.TestRun, project.Key, assembly, testRunner.GetType()));
            var results = testRunner.RunTests(project, assembly);
            runReport.NumberOfTestsPassed += results.Passed.Length;
            runReport.NumberOfTestsFailed += results.Failed.Length;
            runReport.NumberOfTestsIgnored += results.Ignored.Length;
            _bus.Publish(new TestRunMessage(results));
        }
    }
}
