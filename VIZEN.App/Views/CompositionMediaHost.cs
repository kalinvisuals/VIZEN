using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// A media item inside a composition: its own decoder/player for the layer's file. Visible items play
/// (restarting each time they appear, like a scheduled sponsor); hidden ones pause.
/// </summary>
public sealed class CompositionMediaHost : ContentControl
{
    private readonly MediaContentView _view = new();
    private CompositionItemViewModel? _item;
    private bool _loaded;

    public CompositionMediaHost()
    {
        Content = _view;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || DataContext is not CompositionItemViewModel { MediaLayer: { } layer } item)
        {
            return;
        }

        _loaded = true;
        _item = item;
        item.PropertyChanged += OnItemPropertyChanged;
        if (await _view.LoadAsync(layer, reportToLayer: false) is null)
        {
            ApplyVisibility();
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CompositionItemViewModel.IsContentVisible))
        {
            ApplyVisibility();
        }
    }

    private void ApplyVisibility()
    {
        if (_item?.IsContentVisible == true)
        {
            _view.Visibility = Visibility.Visible;
            _view.Play(fromStart: true);
        }
        else
        {
            _view.Visibility = Visibility.Hidden;
            _view.Pause();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemPropertyChanged;
        }

        _view.Unload();
        _loaded = false;
    }
}
