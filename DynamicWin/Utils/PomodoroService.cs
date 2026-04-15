using System;
using System.Timers;

namespace DynamicWin.Utils
{
    public class PomodoroService
    {
        private static PomodoroService _instance;
        public static PomodoroService Instance => _instance ??= new PomodoroService();

        public enum PomodoroState { Idle, Working, ShortBreak, LongBreak }
        public PomodoroState CurrentState { get; private set; } = PomodoroState.Idle;

        private System.Timers.Timer _timer;
        public int RemainingSeconds { get; private set; }
        public int TotalCycleSeconds { get; private set; }
        
        public event Action<int, int> OnTick;
        public event Action<PomodoroState> OnStateChanged;

        private int _workDuration = 25 * 60;
        private int _shortBreakDuration = 5 * 60;
        private int _longBreakDuration = 15 * 60;
        private int _completedCycles = 0;

        public PomodoroService()
        {
            _timer = new System.Timers.Timer(1000);
            _timer.Elapsed += (s, e) => Tick();
        }

        public void StartWork()
        {
            CurrentState = PomodoroState.Working;
            TotalCycleSeconds = _workDuration;
            RemainingSeconds = TotalCycleSeconds;
            OnStateChanged?.Invoke(CurrentState);
            _timer.Start();
        }

        public void StartBreak()
        {
            _completedCycles++;
            CurrentState = (_completedCycles % 4 == 0) ? PomodoroState.LongBreak : PomodoroState.ShortBreak;
            TotalCycleSeconds = (CurrentState == PomodoroState.LongBreak) ? _longBreakDuration : _shortBreakDuration;
            RemainingSeconds = TotalCycleSeconds;
            OnStateChanged?.Invoke(CurrentState);
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            CurrentState = PomodoroState.Idle;
            OnStateChanged?.Invoke(CurrentState);
        }

        public void Toggle()
        {
            if (_timer.Enabled) _timer.Stop();
            else _timer.Start();
        }

        private void Tick()
        {
            if (RemainingSeconds > 0)
            {
                RemainingSeconds--;
                OnTick?.Invoke(RemainingSeconds, TotalCycleSeconds);
            }
            else
            {
                _timer.Stop();
                if (CurrentState == PomodoroState.Working) StartBreak();
                else StartWork();
            }
        }
    }
}
