namespace _0dtes_app.Services;

public static class ObservableExtensions
{
    public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext)
        => source.Subscribe(new Observer<T>(onNext));

    private sealed class Observer<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;

        public Observer(Action<T> onNext)
        {
            _onNext = onNext;
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value) => _onNext(value);
    }
}

public sealed class SimpleSubject<T> : IObservable<T>
{
    private readonly object _gate = new();
    private List<IObserver<T>> _observers = new();

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_gate)
        {
            var observers = new List<IObserver<T>>(_observers) { observer };
            _observers = observers;
            return new Unsubscriber(this, observer);
        }
    }

    public void OnNext(T value)
    {
        IObserver<T>[] snapshot;
        lock (_gate)
        {
            snapshot = _observers.ToArray();
        }
        foreach (var observer in snapshot)
        {
            observer.OnNext(value);
        }
    }

    private void Unsubscribe(IObserver<T> observer)
    {
        lock (_gate)
        {
            var observers = new List<IObserver<T>>(_observers);
            observers.Remove(observer);
            _observers = observers;
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly SimpleSubject<T> _subject;
        private IObserver<T>? _observer;

        public Unsubscriber(SimpleSubject<T> subject, IObserver<T> observer)
        {
            _subject = subject;
            _observer = observer;
        }

        public void Dispose()
        {
            if (_observer is not null)
            {
                _subject.Unsubscribe(_observer);
                _observer = null;
            }
        }
    }
}
