using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Ksp2Redux.Tools.Launcher.Controls
{
    public partial class GroupedComboBox : UserControl
    {
        private readonly ObservableCollection<object> _groupedItems = [];

        /// <summary>
        /// The flattened list of headers and items
        /// </summary>
        public IEnumerable<object> GroupedItems => _groupedItems;

        /// <summary>
        /// The raw source items
        /// </summary>
        public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
            AvaloniaProperty.Register<GroupedComboBox, IEnumerable?>(nameof(ItemsSource));

        /// <summary>
        /// The selected item (only real items will propagate)
        /// </summary>
        public static readonly StyledProperty<object?> SelectedItemProperty =
            AvaloniaProperty.Register<GroupedComboBox, object?>(nameof(SelectedItem));

        /// <summary>
        /// How to extract a group key from each item
        /// </summary>
        public static readonly StyledProperty<Func<object, string>> GroupKeySelectorProperty =
            AvaloniaProperty.Register<GroupedComboBox, Func<object, string>>(nameof(GroupKeySelector));

        public IEnumerable? ItemsSource
        {
            get => GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public object? SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public Func<object, string>? GroupKeySelector
        {
            get => GetValue(GroupKeySelectorProperty);
            set => SetValue(GroupKeySelectorProperty, value);
        }

        static GroupedComboBox()
        {
            ItemsSourceProperty.Changed.AddClassHandler<GroupedComboBox>((x, _) => x.Rebuild());
            GroupKeySelectorProperty.Changed.AddClassHandler<GroupedComboBox>((x, _) => x.Rebuild());
        }

        public GroupedComboBox()
        {
            InitializeComponent();

            GroupKeySelector ??= _ => string.Empty;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void Rebuild()
        {
            _groupedItems.Clear();
            if (ItemsSource == null || GroupKeySelector == null)
            {
                return;
            }

            IOrderedEnumerable<IGrouping<string, object>> groups = ItemsSource.Cast<object>()
                .GroupBy(GroupKeySelector)
                .OrderBy(g => g.Key);

            foreach (IGrouping<string, object> grp in groups)
            {
                _groupedItems.Add(new GroupHeader(grp.Key));
                foreach (object item in grp)
                {
                    _groupedItems.Add(item);
                }
            }
        }
    }

    /// <summary>
    /// Represents a non-selectable group header
    /// </summary>
    /// <param name="key">The key for the group</param>
    public class GroupHeader(string key) : IGroupedComboBoxItem
    {
        public string Key { get; } = key;
        public bool IsSelectable => false;
    }

    /// <summary>
    /// Interface for items that can be enabled/disabled
    /// </summary>
    public interface IGroupedComboBoxItem
    {
        bool IsSelectable { get; }
    }
}