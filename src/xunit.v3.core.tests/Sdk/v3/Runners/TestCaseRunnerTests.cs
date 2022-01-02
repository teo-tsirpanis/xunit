﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

public class TestCaseRunnerTests
{
	[Fact]
	public static async void Messages()
	{
		var summary = new RunSummary { Total = 4, Failed = 2, Skipped = 1, Time = 21.12m };
		var messageBus = new SpyMessageBus();
		var runner = TestableTestCaseRunner.Create(messageBus, result: summary);

		var result = await runner.RunAsync();

		Assert.Same(result, summary);
		Assert.False(runner.TokenSource.IsCancellationRequested);
		Assert.Collection(
			messageBus.Messages,
			msg => Assert.IsAssignableFrom<_TestCaseStarting>(msg),
			msg =>
			{
				var testCaseFinished = Assert.IsAssignableFrom<_TestCaseFinished>(msg);
				Assert.Equal(21.12m, testCaseFinished.ExecutionTime);
				Assert.Equal(4, testCaseFinished.TestsRun);
				Assert.Equal(2, testCaseFinished.TestsFailed);
				Assert.Equal(1, testCaseFinished.TestsSkipped);
			}
		);
	}

	[Fact]
	public static async void FailureInQueueOfTestCaseStarting_DoesNotQueueTestCaseFinished_DoesNotRunTests()
	{
		var messages = new List<_MessageSinkMessage>();
		var messageBus = Substitute.For<IMessageBus>();
		messageBus
			.QueueMessage(null!)
			.ReturnsForAnyArgs(callInfo =>
			{
				var msg = callInfo.Arg<_MessageSinkMessage>();
				messages.Add(msg);

				if (msg is _TestCaseStarting)
					throw new InvalidOperationException();

				return true;
			});
		var runner = TestableTestCaseRunner.Create(messageBus);

		var ex = await Record.ExceptionAsync(() => runner.RunAsync());

		Assert.IsType<InvalidOperationException>(ex);
		var starting = Assert.Single(messages);
		Assert.IsAssignableFrom<_TestCaseStarting>(starting);
		Assert.False(runner.RunTestsAsync_Called);
	}

	[Fact]
	public static async void RunTestAsync_AggregatorIncludesPassedInExceptions()
	{
		var messageBus = new SpyMessageBus();
		var ex = new DivideByZeroException();
		var runner = TestableTestCaseRunner.Create(messageBus, aggregatorSeedException: ex);

		await runner.RunAsync();

		Assert.Same(ex, runner.RunTestsAsync_AggregatorResult);
		Assert.Empty(messageBus.Messages.OfType<_TestCaseCleanupFailure>());
	}

	[Fact]
	public static async void FailureInAfterTestCaseStarting_GivesErroredAggregatorToTestRunner_NoCleanupFailureMessage()
	{
		var messageBus = new SpyMessageBus();
		var runner = TestableTestCaseRunner.Create(messageBus);
		var ex = new DivideByZeroException();
		runner.AfterTestCaseStarting_Callback = aggregator => aggregator.Add(ex);

		await runner.RunAsync();

		Assert.Same(ex, runner.RunTestsAsync_AggregatorResult);
		Assert.Empty(messageBus.Messages.OfType<_TestCaseCleanupFailure>());
	}

	[Fact]
	public static async void FailureInBeforeTestCaseFinished_ReportsCleanupFailure_DoesNotIncludeExceptionsFromAfterTestCaseStarting()
	{
		var messageBus = new SpyMessageBus();
		var testCase = Mocks.TestCase<TestAssemblyRunnerTests.RunAsync>("Messages");
		var runner = TestableTestCaseRunner.Create(messageBus, testCase);
		var startingException = new DivideByZeroException();
		var finishedException = new InvalidOperationException();
		runner.AfterTestCaseStarting_Callback = aggregator => aggregator.Add(startingException);
		runner.BeforeTestCaseFinished_Callback = aggregator => aggregator.Add(finishedException);

		await runner.RunAsync();

		var cleanupFailure = Assert.Single(messageBus.Messages.OfType<_TestCaseCleanupFailure>());
		Assert.Equal(typeof(InvalidOperationException).FullName, cleanupFailure.ExceptionTypes.Single());
	}

	[Fact]
	public static async void Cancellation_TestCaseStarting_DoesNotCallExtensibilityMethods()
	{
		var messageBus = new SpyMessageBus(msg => !(msg is _TestCaseStarting));
		var runner = TestableTestCaseRunner.Create(messageBus);

		await runner.RunAsync();

		Assert.False(runner.AfterTestCaseStarting_Called);
		Assert.False(runner.BeforeTestCaseFinished_Called);
	}

	[Fact]
	public static async void Cancellation_TestCaseFinished_CallsExtensibilityMethods()
	{
		var messageBus = new SpyMessageBus(msg => !(msg is _TestCaseFinished));
		var runner = TestableTestCaseRunner.Create(messageBus);

		await runner.RunAsync();

		Assert.True(runner.AfterTestCaseStarting_Called);
		Assert.True(runner.BeforeTestCaseFinished_Called);
	}

	[Fact]
	public static async void Cancellation_TestClassCleanupFailure_SetsCancellationToken()
	{
		var messageBus = new SpyMessageBus(msg => !(msg is _TestCaseCleanupFailure));
		var runner = TestableTestCaseRunner.Create(messageBus);
		runner.BeforeTestCaseFinished_Callback = aggregator => aggregator.Add(new Exception());

		await runner.RunAsync();

		Assert.True(runner.TokenSource.IsCancellationRequested);
	}

	[Theory(DisableDiscoveryEnumeration = true)]
	[InlineData(typeof(_TestCaseStarting))]
	[InlineData(typeof(_TestCaseFinished))]
	public static async void Cancellation_TriggersCancellationTokenSource(Type messageTypeToCancelOn)
	{
		var messageBus = new SpyMessageBus(msg => !(messageTypeToCancelOn.IsAssignableFrom(msg.GetType())));
		var runner = TestableTestCaseRunner.Create(messageBus);

		await runner.RunAsync();

		Assert.True(runner.TokenSource.IsCancellationRequested);
	}

	[Fact]
	public static async void TestContextInspection()
	{
		var runner = TestableTestCaseRunner.Create();

		await runner.RunAsync();

		Assert.NotNull(runner.AfterTestCaseStarting_Context);
		Assert.Equal(TestEngineStatus.Running, runner.AfterTestCaseStarting_Context.TestAssemblyStatus);
		Assert.Equal(TestEngineStatus.Running, runner.AfterTestCaseStarting_Context.TestCollectionStatus);
		Assert.Equal(TestEngineStatus.Running, runner.AfterTestCaseStarting_Context.TestClassStatus);
		Assert.Equal(TestEngineStatus.Running, runner.AfterTestCaseStarting_Context.TestMethodStatus);
		Assert.Equal(TestEngineStatus.Initializing, runner.AfterTestCaseStarting_Context.TestCaseStatus);
		Assert.Null(runner.AfterTestCaseStarting_Context.TestStatus);
		Assert.Same(runner.TestCase, runner.AfterTestCaseStarting_Context.TestCase);

		Assert.NotNull(runner.RunTestsAsync_Context);
		Assert.Equal(TestEngineStatus.Running, runner.RunTestsAsync_Context.TestAssemblyStatus);
		Assert.Equal(TestEngineStatus.Running, runner.RunTestsAsync_Context.TestCollectionStatus);
		Assert.Equal(TestEngineStatus.Running, runner.RunTestsAsync_Context.TestClassStatus);
		Assert.Equal(TestEngineStatus.Running, runner.RunTestsAsync_Context.TestMethodStatus);
		Assert.Equal(TestEngineStatus.Running, runner.RunTestsAsync_Context.TestCaseStatus);
		Assert.Null(runner.RunTestsAsync_Context.TestStatus);
		Assert.Same(runner.TestCase, runner.RunTestsAsync_Context.TestCase);

		Assert.NotNull(runner.BeforeTestCaseStarting_Context);
		Assert.Equal(TestEngineStatus.Running, runner.BeforeTestCaseStarting_Context.TestAssemblyStatus);
		Assert.Equal(TestEngineStatus.Running, runner.BeforeTestCaseStarting_Context.TestCollectionStatus);
		Assert.Equal(TestEngineStatus.Running, runner.BeforeTestCaseStarting_Context.TestClassStatus);
		Assert.Equal(TestEngineStatus.Running, runner.BeforeTestCaseStarting_Context.TestMethodStatus);
		Assert.Equal(TestEngineStatus.CleaningUp, runner.BeforeTestCaseStarting_Context.TestCaseStatus);
		Assert.Null(runner.BeforeTestCaseStarting_Context.TestStatus);
		Assert.Same(runner.TestCase, runner.BeforeTestCaseStarting_Context.TestCase);
	}

	class TestableTestCaseRunner : TestCaseRunner<TestCaseRunnerContext<_ITestCase>, _ITestCase>
	{
		readonly ExceptionAggregator aggregator;
		readonly IMessageBus messageBus;
		readonly RunSummary result;

		public Action<ExceptionAggregator> AfterTestCaseStarting_Callback = _ => { };
		public bool AfterTestCaseStarting_Called;
		public TestContext? AfterTestCaseStarting_Context;
		public Action<ExceptionAggregator> BeforeTestCaseFinished_Callback = _ => { };
		public bool BeforeTestCaseFinished_Called;
		public TestContext? BeforeTestCaseStarting_Context;
		public Exception? RunTestsAsync_AggregatorResult;
		public bool RunTestsAsync_Called;
		public TestContext? RunTestsAsync_Context;
		public _ITestCase TestCase;
		public CancellationTokenSource TokenSource;

		TestableTestCaseRunner(
			_ITestCase testCase,
			IMessageBus messageBus,
			ExceptionAggregator aggregator,
			CancellationTokenSource tokenSource,
			RunSummary result)
		{
			this.messageBus = messageBus;
			this.aggregator = aggregator;
			this.result = result;

			TestCase = testCase;
			TokenSource = tokenSource;
		}

		public static TestableTestCaseRunner Create(
			IMessageBus? messageBus = null,
			_ITestCase? testCase = null,
			RunSummary? result = null,
			Exception? aggregatorSeedException = null)
		{
			var aggregator = new ExceptionAggregator();
			if (aggregatorSeedException != null)
				aggregator.Add(aggregatorSeedException);

			return new(
				testCase ?? Mocks.TestCase<object>("ToString"),
				messageBus ?? new SpyMessageBus(),
				aggregator,
				new CancellationTokenSource(),
				result ?? new RunSummary()
			);
		}

		protected override ValueTask AfterTestCaseStartingAsync(TestCaseRunnerContext<_ITestCase> ctxt)
		{
			AfterTestCaseStarting_Called = true;
			AfterTestCaseStarting_Context = TestContext.Current;
			AfterTestCaseStarting_Callback(ctxt.Aggregator);
			return default;
		}

		protected override ValueTask BeforeTestCaseFinishedAsync(TestCaseRunnerContext<_ITestCase> ctxt)
		{
			BeforeTestCaseFinished_Called = true;
			BeforeTestCaseStarting_Context = TestContext.Current;
			BeforeTestCaseFinished_Callback(ctxt.Aggregator);
			return default;
		}

		public ValueTask<RunSummary> RunAsync()
			=> RunAsync(new(TestCase, messageBus, aggregator, TokenSource));

		protected override ValueTask<RunSummary> RunTestsAsync(TestCaseRunnerContext<_ITestCase> ctxt)
		{
			RunTestsAsync_AggregatorResult = ctxt.Aggregator.ToException();
			RunTestsAsync_Called = true;
			RunTestsAsync_Context = TestContext.Current;
			return new(result);
		}
	}
}