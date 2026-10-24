using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Graphics.D3D11;

public static class UwpD3D11WindowTelemetry
{
	private static long _activeSessionId;
	private static long _presentEntries;

	private static long _compatibilityAttempts;

	private static long _compatibilitySuccesses;

	private static long _nativeFallbacks;

	private static long _directAttempts;

	private static long _directSuccesses;

	private static long _lastEntryAt;

	private static long _lastCompletedAt;

	private static int _activeStage;

	private static int _activeThread;

	public static long ActiveSessionId => Interlocked.Read(ref _activeSessionId);

	public static void BeginSession(long sessionId)
	{
		Interlocked.Exchange(ref _activeSessionId, sessionId);
		Interlocked.Exchange(ref _presentEntries, 0);
		Interlocked.Exchange(ref _compatibilityAttempts, 0);
		Interlocked.Exchange(ref _compatibilitySuccesses, 0);
		Interlocked.Exchange(ref _nativeFallbacks, 0);
		Interlocked.Exchange(ref _directAttempts, 0);
		Interlocked.Exchange(ref _directSuccesses, 0);
		Interlocked.Exchange(ref _lastEntryAt, 0);
		Interlocked.Exchange(ref _lastCompletedAt, 0);
		Volatile.Write(ref _activeStage, 0);
		Volatile.Write(ref _activeThread, 0);
	}

	public static void EndSession(long sessionId)
	{
		if (Interlocked.CompareExchange(ref _activeSessionId, 0, sessionId) == sessionId)
		{
			Volatile.Write(ref _activeStage, 0);
			Volatile.Write(ref _activeThread, 0);
		}
	}

	internal static void Enter(long sessionId)
	{
		if (sessionId != ActiveSessionId) return;
		Interlocked.Increment(ref _presentEntries);
		Interlocked.Exchange(ref _lastEntryAt, Environment.TickCount64);
		Volatile.Write(ref _activeThread, Environment.CurrentManagedThreadId);
		Volatile.Write(ref _activeStage, 1);
	}

	internal static void CompatibilityAttempt(long sessionId)
	{
		if (sessionId != ActiveSessionId) return;
		Interlocked.Increment(ref _compatibilityAttempts);
	}

	internal static void DirectAttempt(long sessionId)
	{
		if (sessionId != ActiveSessionId) return;
		Interlocked.Increment(ref _directAttempts);
	}

	internal static void DirectCompleted(long sessionId, bool success)
	{
		if (sessionId != ActiveSessionId) return;
		if (success)
		{
			Interlocked.Increment(ref _directSuccesses);
		}
	}

	internal static void CompatibilityCompleted(long sessionId, bool success)
	{
		if (sessionId != ActiveSessionId) return;
		if (success)
		{
			Interlocked.Increment(ref _compatibilitySuccesses);
		}
		Volatile.Write(ref _activeStage, success ? 3 : 2);
	}

	internal static void NativeFallback(long sessionId)
	{
		if (sessionId != ActiveSessionId) return;
		Interlocked.Increment(ref _nativeFallbacks);
	}

	internal static void Complete(long sessionId)
	{
		if (sessionId != ActiveSessionId) return;
		Interlocked.Exchange(ref _lastCompletedAt, Environment.TickCount64);
		Volatile.Write(ref _activeStage, 0);
		Volatile.Write(ref _activeThread, 0);
	}

	public static string GetSnapshot()
	{
		InlineArray10<object> buffer = default(InlineArray10<object>);
		buffer[0] = Interlocked.Read(in _presentEntries);
		buffer[1] = Interlocked.Read(in _directSuccesses);
		buffer[2] = Interlocked.Read(in _directAttempts);
		buffer[3] = Interlocked.Read(in _compatibilityAttempts);
		buffer[4] = Interlocked.Read(in _compatibilitySuccesses);
		buffer[5] = Interlocked.Read(in _nativeFallbacks);
		buffer[6] = Volatile.Read(in _activeStage);
		buffer[7] = Volatile.Read(in _activeThread);
		buffer[8] = Interlocked.Read(in _lastEntryAt);
		buffer[9] = Interlocked.Read(in _lastCompletedAt);
		return $"sessionId={ActiveSessionId}, " + string.Format("windowPresentEntries={0}, direct={1}/{2}, compatibilityAttempts={3}, compatibilitySuccesses={4}, nativeFallbacks={5}, activeStage={6}, activeThread={7}, lastEntry={8}, lastCompleted={9}", (ReadOnlySpan<object?>)buffer);
	}
}
