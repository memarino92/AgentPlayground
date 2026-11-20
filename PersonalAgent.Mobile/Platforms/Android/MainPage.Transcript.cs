using Android.Views;
using AndroidX.RecyclerView.Widget;

namespace PersonalAgent.Mobile;

public partial class MainPage
{
    private TranscriptScrollListener? _transcriptScrollListener;

    private void RequestTranscriptFollow() => _transcriptScrollListener?.RequestFollow();

    private void OnTranscriptSizeChanged(object? sender, EventArgs e) => RequestTranscriptFollow();

    private void OnTranscriptLoaded(object? sender, EventArgs e)
    {
        if (_transcriptScrollListener is not null || Transcript.Handler?.PlatformView is not RecyclerView recycler) return;
        _transcriptScrollListener = new TranscriptScrollListener(this, recycler);
        _transcriptScrollListener.Attach();
    }

    private void OnTranscriptUnloaded(object? sender, EventArgs e)
    {
        _transcriptScrollListener?.Detach();
        _transcriptScrollListener = null;
    }

    private sealed class TranscriptScrollListener(MainPage page, RecyclerView recycler)
        : RecyclerView.OnScrollListener, ViewTreeObserver.IOnPreDrawListener
    {
        private ViewTreeObserver? _observer;
        private bool _followRequested = true;
        private bool _positionRequested;

        public void RequestFollow()
        {
            _followRequested = true;
            _positionRequested = false;
        }

        public void Attach()
        {
            recycler.AddOnScrollListener(this);
            _observer = recycler.ViewTreeObserver;
            _observer?.AddOnPreDrawListener(this);
        }

        public void Detach()
        {
            recycler.RemoveOnScrollListener(this);
            var observer = _observer is { IsAlive: true } ? _observer : recycler.ViewTreeObserver;
            if (observer is { IsAlive: true }) observer.RemoveOnPreDrawListener(this);
        }

        public override void OnScrolled(RecyclerView recyclerView, int dx, int dy)
        {
            if (dy != 0 && recyclerView.ScrollState != RecyclerView.ScrollStateIdle)
                page._followTranscript = !recyclerView.CanScrollVertically(1);
        }

        public bool OnPreDraw()
        {
            if (!_followRequested || !page._followTranscript || recycler.ScrollState != RecyclerView.ScrollStateIdle ||
                recycler.Height <= 0 ||
                !recycler.CanScrollVertically(1) || recycler.GetAdapter() is not { ItemCount: > 0 } adapter ||
                recycler.GetLayoutManager() is not LinearLayoutManager layout) return true;

            // Align the footer after layout, when its current height is known.
            _followRequested = false;
            var lastPosition = adapter.ItemCount - 1;
            if (layout.FindViewByPosition(lastPosition) is { } last)
            {
                var offset = layout.GetDecoratedBottom(last) - (recycler.Height - recycler.PaddingBottom);
                if (offset > 0) recycler.ScrollBy(0, offset);
            }
            else if (!_positionRequested)
            {
                _positionRequested = _followRequested = true;
                recycler.ScrollToPosition(lastPosition);
            }

            return true;
        }
    }
}
