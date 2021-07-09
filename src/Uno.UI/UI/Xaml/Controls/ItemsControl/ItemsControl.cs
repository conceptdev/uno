﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Uno.Extensions;
using Uno.UI;
using System.Collections.Specialized;
using Uno.Disposables;
using Uno.UI.Controls;
using Windows.UI.Xaml.Markup;
using Uno.UI.DataBinding;
using Windows.UI.Xaml.Data;
using Windows.Foundation.Collections;
using Uno.Extensions.Specialized;
using Microsoft.Extensions.Logging;
using Uno.UI.Extensions;
using System.ComponentModel;
using Windows.UI.Xaml.Controls.Primitives;

#if XAMARIN_ANDROID
using View = Android.Views.View;
using _ViewGroup = Android.Views.ViewGroup;
using Font = Android.Graphics.Typeface;
using Android.Graphics;
#elif XAMARIN_IOS
using View = UIKit.UIView;
using _ViewGroup = UIKit.UIView;
using Color = UIKit.UIColor;
using Font = UIKit.UIFont;
using UIKit;
#elif __MACOS__
using AppKit;
using View = AppKit.NSView;
using Color = Windows.UI.Color;
#else
using Color = System.Drawing.Color;
using View = Windows.UI.Xaml.UIElement;
#endif

namespace Windows.UI.Xaml.Controls
{
	[ContentProperty(Name = "Items")]
	public partial class ItemsControl : Control, IItemsControl
	{
		protected IVectorChangedEventArgs _inProgressVectorChange;

		private ItemsPresenter _itemsPresenter;

		private readonly SerialDisposable _notifyCollectionChanged = new SerialDisposable();
		private readonly SerialDisposable _notifyCollectionGroupsChanged = new SerialDisposable();
		private readonly SerialDisposable _cvsViewChanged = new SerialDisposable();

		private bool _isReady; // Template applied
		private bool _needsUpdateItems;
		private ItemCollection _items = new ItemCollection();

		// This gets prepended to MaterializedContainers to ensure it's being considered 
		// even if it might not yet have be added to the ItemsPanel (e.eg., NativeListViewBase).
		private DependencyObject _containerBeingPrepared;

		private int[] _groupCounts;

		/// <summary>
		/// This template is stored here in order to allow for 
		/// FrameworkTemplate pooling to function properly when an ItemTemplateSelector has been
		/// specified.
		/// </summary>
		private readonly static DataTemplate InnerContentPresenterTemplate = new DataTemplate(() => new ContentPresenter());

		public static ItemsControl GetItemsOwner(DependencyObject element)
		{
			// ItemsControl is provided ONLY for the ItemsHost panel
			// Neither the ItemsPresenter nor an item container will give a non null result.
			var panel = element as Panel;
			if (panel != null && panel.IsItemsHost)
			{
				return panel.ItemsOwner;
			}
			else
			{
				return null;
			}
		}

		public ItemsControl()
		{
			Initialize();

			DefaultStyleKey = typeof(ItemsControl);
		}

		void Initialize()
		{
			InitializePartial();

			_items.VectorChanged += OnItemsVectorChanged;
		}

		private void OnItemsVectorChanged(IObservableVector<object> sender, IVectorChangedEventArgs e)
		{
#if !HAS_EXPENSIVE_TRYFINALLY // Try/finally incurs a very large performance hit in mono-wasm - https://github.com/dotnet/runtime/issues/50783
			try
#endif
			{
				_inProgressVectorChange = e;
				OnItemsChanged(e);
			}
#if !HAS_EXPENSIVE_TRYFINALLY // Try/finally incurs a very large performance hit in mono-wasm - https://github.com/dotnet/runtime/issues/50783
			finally
#endif
			{
				_inProgressVectorChange = null;
			}
			SetNeedsUpdateItems();
		}

		partial void InitializePartial();
		partial void RequestLayoutPartial();
		partial void RemoveViewPartial(View current);

		private View _internalItemsPanelRoot;
		/// <summary>
		/// The actual View that acts as the ItemsPanelRoot (used by FlipView and ListView, which don't use actual Panels)
		/// </summary>
		internal View InternalItemsPanelRoot
		{
			get { return _internalItemsPanelRoot; }
			set
			{
				if (_internalItemsPanelRoot is IDependencyObjectStoreProvider provider)
				{
					provider.SetParent(null);
				}

				_internalItemsPanelRoot = value;
			}
		}

		private Panel _itemsPanelRoot;
		public Panel ItemsPanelRoot
		{
			get { return _itemsPanelRoot; }
			set
			{
				if (_itemsPanelRoot is IDependencyObjectStoreProvider provider)
				{
					provider.SetParent(null);
				}

				_itemsPanelRoot = value;
			}
		}

		/// <summary>
		/// True if ItemsControl should manage the children of ItemsPanel, false if this is handled by an inheriting class.
		/// </summary>
		private protected virtual bool ShouldItemsControlManageChildren { get { return ItemsPanelRoot == InternalItemsPanelRoot; } }

		#region ItemsPanel DependencyProperty

		public ItemsPanelTemplate ItemsPanel
		{
			get { return (ItemsPanelTemplate)GetValue(ItemsPanelProperty); }
			set { SetValue(ItemsPanelProperty, value); }
		}

		public static DependencyProperty ItemsPanelProperty { get; } =
			DependencyProperty.Register(
				"ItemsPanel",
				typeof(ItemsPanelTemplate),
				typeof(ItemsControl),
				new FrameworkPropertyMetadata(
					(ItemsPanelTemplate)null,
					FrameworkPropertyMetadataOptions.ValueDoesNotInheritDataContext,
					(s, e) => ((ItemsControl)s)?.OnItemsPanelChanged((ItemsPanelTemplate)e.OldValue, (ItemsPanelTemplate)e.NewValue)
				)
			);

		private void OnItemsPanelChanged(ItemsPanelTemplate oldItemsPanel, ItemsPanelTemplate newItemsPanel)
		{
			if (_isReady && !Equals(oldItemsPanel, newItemsPanel)) // Panel is created on ApplyTemplate, so do not create it twice (first on set PanelTemplate, second on ApplyTemplate)
			{
				UpdateItemsPanelRoot();
			}
		}

		#endregion

		#region ItemTemplate DependencyProperty

		public DataTemplate ItemTemplate
		{
			get { return (DataTemplate)GetValue(ItemTemplateProperty); }
			set { SetValue(ItemTemplateProperty, value); }
		}

		public static DependencyProperty ItemTemplateProperty { get; } =
			DependencyProperty.Register(
				"ItemTemplate",
				typeof(DataTemplate),
				typeof(ItemsControl),
				new FrameworkPropertyMetadata(
					(DataTemplate)null,
					FrameworkPropertyMetadataOptions.ValueDoesNotInheritDataContext,
					(s, e) => ((ItemsControl)s)?.OnItemTemplateChanged((DataTemplate)e.OldValue, (DataTemplate)e.NewValue)
				)
			);

		protected virtual void OnItemTemplateChanged(DataTemplate oldItemTemplate, DataTemplate newItemTemplate)
		{
			Refresh();
			SetNeedsUpdateItems();
		}

		#endregion

		#region ItemTemplateSelector DependencyProperty

		public DataTemplateSelector ItemTemplateSelector
		{
			get { return (DataTemplateSelector)GetValue(ItemTemplateSelectorProperty); }
			set { SetValue(ItemTemplateSelectorProperty, value); }
		}

		public static DependencyProperty ItemTemplateSelectorProperty { get; } =
			DependencyProperty.Register(
				"ItemTemplateSelector",
				typeof(DataTemplateSelector),
				typeof(ItemsControl),
				new FrameworkPropertyMetadata(
					(DataTemplateSelector)null,
					(s, e) => ((ItemsControl)s)?.OnItemTemplateSelectorChanged((DataTemplateSelector)e.OldValue, (DataTemplateSelector)e.NewValue)
				)
			);

		protected virtual void OnItemTemplateSelectorChanged(DataTemplateSelector oldItemTemplateSelector, DataTemplateSelector newItemTemplateSelector)
		{
			Refresh();
			SetNeedsUpdateItems();
		}

		#endregion

		#region ItemsSource DependencyProperty
		public object ItemsSource
		{
			get { return (object)this.GetValue(ItemsSourceProperty); }
			set { this.SetValue(ItemsSourceProperty, value); }
		}

		public static DependencyProperty ItemsSourceProperty { get; } =
			DependencyProperty.Register(
				"ItemsSource",
				typeof(object),
				typeof(ItemsControl),
				new FrameworkPropertyMetadata(null, (s, e) => ((ItemsControl)s).OnItemsSourceChanged(e))
		);
		#endregion

		public ItemCollection Items => _items;

		protected virtual void OnItemsChanged(object e)
		{
		}

		/// <summary>
		/// The number of groups in the <see cref="ItemsSource"/>, zero if it is ungrouped.
		/// </summary>
		internal int NumberOfGroups => CollectionGroups?.Count ?? 0;

		internal int NumberOfDisplayGroups
		{
			get
			{
				if (!AreEmptyGroupsHidden)
				{
					return NumberOfGroups;
				}

				return CollectionGroups.Where(g => (g as ICollectionViewGroup).GroupItems.Count > 0).Count();
			}
		}

		/// <summary>
		/// The number of items in the source.
		/// </summary>
		internal int NumberOfItems
		{
			get
			{
				var items = GetItems();
				if (items is ICollection<object> list)
				{
					// ICollectionView optimization
					return list.Count;
				}
				return items?.Count() ?? 0;
			}
		}

		internal bool HasItems
		{
			get
			{
				var items = GetItems();
				if (items is ICollection<object> collection)
				{
					// ICollectionView optimization
					return collection.Count > 0;
				}

				return items?.Any() ?? false;
			}
		}

		//Return either the contents of ItemsSource or of Items. This method will be obsolete when Items' contents properly reflect ItemsSource
		internal IEnumerable GetItems()
		{
			var unwrappedSource = UnwrapItemsSource();

			if (unwrappedSource != null)
			{
				return unwrappedSource as IEnumerable;
			}
			if (Items?.Any() ?? false)
			{
				return Items;
			}
			return Enumerable.Empty<object>();
		}

		#region ItemContainerStyle DependencyProperty
		public Style ItemContainerStyle
		{
			get { return (Style)GetValue(ItemContainerStyleProperty); }
			set { SetValue(ItemContainerStyleProperty, value); }
		}

		public static DependencyProperty ItemContainerStyleProperty { get; } =
					DependencyProperty.Register("ItemContainerStyle", typeof(Style), typeof(ItemsControl), new FrameworkPropertyMetadata(
						default(Style),
						FrameworkPropertyMetadataOptions.ValueDoesNotInheritDataContext,
						(o, e) => ((ItemsControl)o).OnItemContainerStyleChanged((Style)e.OldValue, (Style)e.NewValue))
					);

		protected virtual void OnItemContainerStyleChanged(Style oldItemContainerStyle, Style newItemContainerStyle) => Refresh();
		#endregion

		#region ItemContainerStyleSelector DependencyProperty
		public StyleSelector ItemContainerStyleSelector
		{
			get { return (StyleSelector)GetValue(ItemContainerStyleSelectorProperty); }
			set { SetValue(ItemContainerStyleSelectorProperty, value); }
		}

		public static DependencyProperty ItemContainerStyleSelectorProperty { get; } =
			DependencyProperty.Register("ItemContainerStyleSelector", typeof(StyleSelector), typeof(ItemsControl), new FrameworkPropertyMetadata(
				default(StyleSelector),
				(o, e) => ((ItemsControl)o).OnItemContainerStyleSelectorChanged((StyleSelector)e.OldValue, (StyleSelector)e.NewValue))
			);

		protected virtual void OnItemContainerStyleSelectorChanged(StyleSelector oldItemContainerStyleSelector, StyleSelector newItemContainerStyleSelector) => Refresh();

		#endregion

		public IObservableVector<GroupStyle> GroupStyle { get; } = new ObservableVector<GroupStyle>();

		#region IsGrouping DependencyProperty
		public bool IsGrouping
		{
			get { return (bool)GetValue(IsGroupingProperty); }
			private set { SetValue(IsGroupingProperty, value); }
		}

		public static DependencyProperty IsGroupingProperty { get; } =
			DependencyProperty.Register("IsGrouping", typeof(bool), typeof(ItemsControl), new FrameworkPropertyMetadata(false));
		#endregion

		#region Internal Attached Properties

		internal static DependencyProperty IndexForItemContainerProperty { get; } =
			DependencyProperty.RegisterAttached(
				"IndexForItemContainer",
				typeof(int),
				typeof(ItemsControl),
				new FrameworkPropertyMetadata(-1)
			);

		internal static DependencyProperty ItemsControlForItemContainerProperty { get; } =
			DependencyProperty.RegisterAttached(
				"ItemsControlForItemContainer",
				typeof(WeakReference<ItemsControl>),
				typeof(ItemsControl),
				new FrameworkPropertyMetadata(DependencyProperty.UnsetValue)
			);

		#endregion

		internal bool AreEmptyGroupsHidden => CollectionGroups != null && (GroupStyle.FirstOrDefault()?.HidesIfEmpty ?? false);

		private IObservableVector<object> CollectionGroups => (GetItems() as ICollectionView)?.CollectionGroups;


		internal GroupStyle GetGroupStyle()
		{
			return GroupStyle.FirstOrDefault();
		}

		/// <summary>
		/// Get the index for the next item after <paramref name="currentItem"/>.
		/// </summary>
		internal Uno.UI.IndexPath? GetNextItemIndex(Uno.UI.IndexPath? currentItem, int direction)
		{
			if (!HasItems)
			{
				return null;
			}

			if (currentItem == null)
			{
				// Null is treated as 'just before the first item.' 
				if (direction == 1)
				{
					var firstNonEmptySection = IsGrouping ? GetNextNonEmptySection(-1, 1).Value : 0;
					return Uno.UI.IndexPath.FromRowSection(0, firstNonEmptySection);
				}
				else
				{
					return null;
				}
			}
			if (direction == 1)
			{
				return GetIncrementedItemIndex(currentItem.Value);
			}

			if (direction == -1)
			{
				return GetDecrementedItemIndex(currentItem.Value);
			}
			throw new ArgumentOutOfRangeException(nameof(direction));
		}

		private Uno.UI.IndexPath? GetIncrementedItemIndex(Uno.UI.IndexPath currentItem)
		{
			if (!IsGrouping)
			{
				if (currentItem.Section > 0)
				{
					throw new InvalidOperationException("Received an index with non-zero group, but source is not grouped.");
				}

				if (currentItem.Row == NumberOfItems - 1)
				{
					return null;
				}
				return Uno.UI.IndexPath.FromRowSection(currentItem.Row + 1, 0);
			}

			if (currentItem.Row == GetDisplayGroupCount(currentItem.Section) - 1)
			{
				var nextSection = GetNextNonEmptySection(currentItem.Section, 1);
				if (!nextSection.HasValue)
				{
					//No more non-empty sections
					return null;
				}
				return Uno.UI.IndexPath.FromRowSection(0, nextSection.Value);
			}
			return Uno.UI.IndexPath.FromRowSection(currentItem.Row + 1, currentItem.Section);
		}

		private Uno.UI.IndexPath? GetDecrementedItemIndex(Uno.UI.IndexPath currentItem)
		{
			if (!IsGrouping)
			{
				if (currentItem.Section > 0)
				{
					throw new InvalidOperationException("Received an index with non-zero group, but source is not grouped.");
				}
				if (currentItem.Row == 0)
				{
					return null;
				}
				return Uno.UI.IndexPath.FromRowSection(currentItem.Row - 1, 0);
			}
			if (currentItem.Row == 0)
			{
				var nextSection = GetNextNonEmptySection(currentItem.Section, -1);
				if (!nextSection.HasValue)
				{
					//No previous non-empty sections
					return null;
				}
				return Uno.UI.IndexPath.FromRowSection(GetDisplayGroupCount(nextSection.Value) - 1, nextSection.Value);
			}
			return Uno.UI.IndexPath.FromRowSection(currentItem.Row - 1, currentItem.Section);
		}

		/// <summary>
		/// Gets a flattened item index. Unlike <see cref="GetDisplayIndexFromIndexPath(IndexPath)"/>, this can not be overridden to adjust for 
		/// supplementary elements (eg headers) on derived controls. This represents the (flattened) index in the data source as opposed
		/// to the 'display' index.
		/// 
		/// Note that the <see cref="IndexPath"/> is still the 'display' value in that it doesn't 'know about' empty groups if HidesIfEmpty is set to true.
		/// </summary>
		internal int GetIndexFromIndexPath(Uno.UI.IndexPath indexPath)
		{
			if (indexPath.Section == 0)
			{
				return indexPath.Row;
			}

			var itemsInPrecedingSections = 0;
			for (int i = 0; i < indexPath.Section; i++)
			{
				itemsInPrecedingSections += GetDisplayGroupCount(i);
			}
			return itemsInPrecedingSections + indexPath.Row;
		}

		/// <summary>
		/// Convert flat item index to (group, index in group)
		/// </summary>
		internal Uno.UI.IndexPath? GetIndexPathFromIndex(int index)
		{
			if (!IsGrouping)
			{
				return Uno.UI.IndexPath.FromRowSection(index, 0);
			}

			int row = index;
			for (int i = 0; i < NumberOfDisplayGroups; i++)
			{
				var groupCount = GetDisplayGroupCount(i);
				if (row < groupCount)
				{
					return Uno.UI.IndexPath.FromRowSection(row, i);
				}
				else
				{
					row -= groupCount;
				}
			}

			return null;
		}

		private int? GetNextNonEmptySection(int startingSection, int direction)
		{
			var i = startingSection;
			i += direction;
			while (i >= 0 && i < NumberOfDisplayGroups)
			{
				if (GetDisplayGroupCount(i) > 0)
				{
					return i;
				}
				i += direction;
			}
			return null;
		}

		/// <summary>
		/// Get the last item in the source, or null if the source is empty.
		/// </summary>
		internal Uno.UI.IndexPath? GetLastItem()
		{
			if (!IsGrouping)
			{
				var itemCount = NumberOfItems;
				if (itemCount <= 0)
				{
					return null;
				}

				return Uno.UI.IndexPath.FromRowSection(itemCount - 1, 0);
			}

			for (int i = NumberOfDisplayGroups - 1; i >= 0; i--)
			{
				var count = GetDisplayGroupCount(i);
				if (count > 0)
				{
					return Uno.UI.IndexPath.FromRowSection(count - 1, i);
				}
			}

			// No non-empty groups
			return null;
		}

		/// <summary>
		/// Return the index of a group in the unfiltered source, adjusted for empty groups hidden from the view by GroupStyle.HidesIfEmpty=true
		/// </summary>
		internal int AdjustGroupIndexForHidesIfEmpty(int index)
		{
			var adjustedIndex = index;

			if (AreEmptyGroupsHidden)
			{
				for (int i = 0; i < index; i++)
				{
					if (GetGroupCount(i) == 0)
					{
						adjustedIndex--;
					}
				}
			}

			return adjustedIndex;
		}

		/// <summary>
		/// Return the number of items to show on the last line of a group, if there are multiple items per line.
		/// </summary>
		internal int GetItemsOnLastLine(int section, int itemsPerLine)
		{
			int remainder = GetDisplayGroupCount(section) % itemsPerLine;
			return remainder == 0 ? itemsPerLine : remainder;
		}

		partial void OnDisplayMemberPathChangedPartial(string oldDisplayMemberPath, string newDisplayMemberPath)
		{
			if (string.IsNullOrEmpty(oldDisplayMemberPath) && string.IsNullOrEmpty(newDisplayMemberPath))
			{
				return; // nothing 
			}

			Refresh();
		}

		protected virtual void OnItemsSourceChanged(DependencyPropertyChangedEventArgs e)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().LogDebug($"Calling OnItemsSourceChanged(), Old source={e.OldValue}, new source={e.NewValue}, NoOfItems={NumberOfItems}");
			}

			IsGrouping = (e.NewValue as ICollectionView)?.CollectionGroups != null;
			Items.SetItemsSource(UnwrapItemsSource() as IEnumerable); // This will call SetNeedsUpdateItems() via Items.VectorChanged
			ObserveCollectionChanged();
			TryObserveCollectionViewSource(e.NewValue);
		}

		private void TryObserveCollectionViewSource(object newValue)
		{
			if (newValue is CollectionViewSource cvs)
			{
				_cvsViewChanged.Disposable = null;
				_cvsViewChanged.Disposable = cvs.RegisterDisposablePropertyChangedCallback(
					CollectionViewSource.ViewProperty,
					(s, e) =>
					{
						ObserveCollectionChanged();
						SetNeedsUpdateItems();
					}
				);
			}
		}

		internal int GetGroupCount(int groupIndex) => IsGrouping ? GetGroupAt(groupIndex).GroupItems.Count : 0;

		internal int GetDisplayGroupCount(int displaySection) => IsGrouping ? GetGroupAtDisplaySection(displaySection).GroupItems.Count : 0;

		// Supports the common usage (prescribed in the official doc) of ItemsSource="{Binding Source = {StaticResource SomeCollectionViewSource}}"
		//
		// Note: this is not correct, in that it's not actually possible on UWP to set ItemsControl.ItemsSource to a CollectionViewSource.
		// What actually happens on UWP in the above case is that the *BindingExpression* 'unwraps' the CollectionViewSource and passes the
		// CollectionViewSource.View to whatever is being bound to (ie the ItemsSource). This should be fixed at some point because it
		// has observable consequences in some usages.
		internal object UnwrapItemsSource()
			=> ItemsSource is CollectionViewSource cvs ? (object)cvs.View : ItemsSource;

		internal void ObserveCollectionChanged()
		{
			var unwrappedSource = UnwrapItemsSource();

			if (unwrappedSource is null)
			{
				_notifyCollectionChanged.Disposable = null;
			}
			//Subscribe to changes on grouped source that is an observable collection
			else if (unwrappedSource is CollectionView collectionView && collectionView.CollectionGroups != null && collectionView.InnerCollection is INotifyCollectionChanged observableGroupedSource)
			{
				// This is a workaround for a bug with EventRegistrationTokenTable on Xamarin, where subscribing/unsubscribing to a class method directly won't 
				// remove the handler.
				NotifyCollectionChangedEventHandler handler = OnItemsSourceGroupsChanged;
				_notifyCollectionChanged.Disposable = Disposable.Create(() =>
					observableGroupedSource.CollectionChanged -= handler
				);
				observableGroupedSource.CollectionChanged += handler;
			}
			//Subscribe to changes on ICollectionView that is grouped
			else if (unwrappedSource is ICollectionView iCollectionView && iCollectionView.CollectionGroups != null)
			{
				// This is a workaround for a bug with EventRegistrationTokenTable on Xamarin, where subscribing/unsubscribing to a class method directly won't 
				// remove the handler.
				VectorChangedEventHandler<object> handler = OnItemsSourceGroupsVectorChanged;
				_notifyCollectionChanged.Disposable = Disposable.Create(() =>
					iCollectionView.CollectionGroups.VectorChanged -= handler
				);
				iCollectionView.CollectionGroups.VectorChanged += handler;

			}
			//Subscribe to changes on observable collection
			else if (unwrappedSource is INotifyCollectionChanged existingObservable)
			{
				// This is a workaround for a bug with EventRegistrationTokenTable on Xamarin, where subscribing/unsubscribing to a class method directly won't 
				// remove the handler.
				NotifyCollectionChangedEventHandler handler = OnItemsSourceCollectionChanged;
				_notifyCollectionChanged.Disposable = Disposable.Create(() =>
					existingObservable.CollectionChanged -= handler
				);
				existingObservable.CollectionChanged += handler;
			}
			else if (unwrappedSource is IObservableVector<object> observableVector)
			{
				// This is a workaround for a bug with EventRegistrationTokenTable on Xamarin, where subscribing/unsubscribing to a class method directly won't 
				// remove the handler.
				VectorChangedEventHandler<object> handler = OnItemsSourceVectorChanged;
				_notifyCollectionChanged.Disposable = Disposable.Create(() =>
					observableVector.VectorChanged -= handler
				);
				observableVector.VectorChanged += handler;
			}
			else if (unwrappedSource is IObservableVector genericObservableVector)
			{
				VectorChangedEventHandler handler = OnItemsSourceVectorChanged;
				_notifyCollectionChanged.Disposable = Disposable.Create(() =>
					genericObservableVector.UntypedVectorChanged -= handler
				);
				genericObservableVector.UntypedVectorChanged += handler;
			}
			else
			{
				_notifyCollectionChanged.Disposable = null;
			}

			_notifyCollectionGroupsChanged.Disposable = null;

			//Subscribe to group changes if they are observable collections
			if (unwrappedSource is ICollectionView collectionViewGrouped && collectionViewGrouped.CollectionGroups != null)
			{
				var disposables = new CompositeDisposable();
				int i = -1;
				foreach (ICollectionViewGroup group in collectionViewGrouped.CollectionGroups)
				{
					//Hidden empty groups shouldn't be counted because they won't appear to UICollectionView
					if (!AreEmptyGroupsHidden || group.GroupItems.Count > 0)
					{
						i++;
					}
					var insideLoop = i;

					// TODO: At present we listen to changes on ICollectionViewGroup.Group, which supports 'observable of observable groups'
					// using CollectionViewSource. The correct way to do this would be for CollectionViewGroup.GroupItems to instead implement 
					// INotifyCollectionChanged.
					INotifyCollectionChanged observableGroup = group.GroupItems as INotifyCollectionChanged ?? group.Group as INotifyCollectionChanged;
					// Prefer INotifyCollectionChanged for, eg, batched item changes
					if (observableGroup != null)
					{
						NotifyCollectionChangedEventHandler onCollectionChanged = (o, e) => OnItemsSourceSingleCollectionChanged(o, e, insideLoop);
						Disposable.Create(() => observableGroup.CollectionChanged -= onCollectionChanged)
							.DisposeWith(disposables);
						observableGroup.CollectionChanged += onCollectionChanged;
					}
					else
					{
						VectorChangedEventHandler<object> onVectorChanged = (o, e) => OnItemsSourceSingleCollectionChanged(o, e.ToNotifyCollectionChangedEventArgs(), insideLoop);
						Disposable.Create(() =>
							group.GroupItems.VectorChanged -= onVectorChanged
						)
							.DisposeWith(disposables);
						group.GroupItems.VectorChanged += onVectorChanged;
					}

					if (group is global::System.ComponentModel.INotifyPropertyChanged bindableGroup)
					{
						Disposable.Create(() =>
							bindableGroup.PropertyChanged -= onPropertyChanged
						)
							.DisposeWith(disposables);
						bindableGroup.PropertyChanged += onPropertyChanged;
						OnGroupPropertyChanged(group, insideLoop);

						void onPropertyChanged(object sender, global::System.ComponentModel.PropertyChangedEventArgs e)
						{
							if (e.PropertyName == "Group")
							{
								OnGroupPropertyChanged(group, insideLoop);
							}
						}
					}
				}
				_notifyCollectionGroupsChanged.Disposable = disposables;

				UpdateGroupCounts();
			}
		}

		private void UpdateGroupCounts()
		{
			if (CollectionGroups != null)
			{
				var counts = new List<int>();
				for (int i = 0; i < CollectionGroups.Count; i++)
				{
					var group = CollectionGroups[i] as ICollectionViewGroup;
					counts.Add(group.GroupItems.Count);
				}
				_groupCounts = counts.ToArray();
			}
			else
			{
				_groupCounts = null;
			}
		}

		/// <summary>
		/// During an update, this will represent the group state immediately prior to the update. 
		/// </summary>
		internal int GetCachedGroupCount(int groupIndex) => _groupCounts[groupIndex];

		private void OnItemsSourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
		{
			OnItemsSourceSingleCollectionChanged(sender, args, section: 0);
		}

		private void OnItemsSourceGroupsVectorChanged(object sender, IVectorChangedEventArgs args) => OnItemsSourceGroupsChanged(sender, args.ToNotifyCollectionChangedEventArgs());

		private void OnItemsSourceVectorChanged(object sender, IVectorChangedEventArgs args) => OnItemsSourceCollectionChanged(sender, args.ToNotifyCollectionChangedEventArgs());

		/// <summary>
		/// Called when a collection change occurs within a single group, or within the entire source if it is ungrouped.
		/// </summary>
		internal virtual void OnItemsSourceSingleCollectionChanged(object sender, NotifyCollectionChangedEventArgs args, int section)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().LogDebug($"Called {nameof(OnItemsSourceSingleCollectionChanged)}(), Action={args.Action}, NoOfItems={NumberOfItems}");
			}
			UpdateItems();
		}

		/// <summary>
		/// Called when a change is made to an observable collection of groups.
		/// </summary>
		internal virtual void OnItemsSourceGroupsChanged(object sender, NotifyCollectionChangedEventArgs args)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().LogDebug($"Called {nameof(OnItemsSourceGroupsChanged)}(), Action={args.Action}, NoOfItems={NumberOfItems}, NoOfGroups={NumberOfGroups}");
			}
			UpdateItems();
		}

		internal virtual void OnGroupPropertyChanged(ICollectionViewGroup group, int groupIndex)
		{

		}

		protected override void OnApplyTemplate()
		{
			base.OnApplyTemplate();

			_isReady = true;

			UpdateItemsPanelRoot();
		}

		private protected override void OnUnloaded()
		{
			base.OnUnloaded();
		}

		private void UpdateItemsPanelRoot()
		{
			// Remove items from the previous ItemsPanelRoot to ensure they can be safely added to the new one
			if (ShouldItemsControlManageChildren)
			{
				ItemsPanelRoot?.Children.Clear();
			}

			if (InternalItemsPanelRoot != null)
			{
				CleanUpInternalItemsPanel(InternalItemsPanelRoot);
			}

			var itemsPanel = ItemsPanel?.LoadContent() ?? new StackPanel();
			InternalItemsPanelRoot = ResolveInternalItemsPanel(itemsPanel);
			ItemsPanelRoot = itemsPanel as Panel;

			ItemsPanelRoot?.SetItemsOwner(this);
			_itemsPresenter?.SetItemsPanel(InternalItemsPanelRoot);

			SetNeedsUpdateItems();
		}

		/// <summary>
		/// Resolve the view to use as <see cref="InternalItemsPanelRoot"/>. Inheriting classes should
		/// override this method if the 'real' displaying panel is different from the panel nominated by ItemsPanelTemplate.
		/// </summary>
		protected virtual View ResolveInternalItemsPanel(View itemsPanel)
		{
			return itemsPanel;
		}

		internal protected override void OnDataContextChanged(DependencyPropertyChangedEventArgs e)
		{
			base.OnDataContextChanged(e);

			SyncDataContext();
		}

		protected virtual void SyncDataContext()
		{
		}

		public void SetNeedsUpdateItems()
		{
			_needsUpdateItems = true;
			UpdateItemsIfNeeded();
			RequestLayoutPartial();
		}

		public bool UpdateItemsIfNeeded()
		{
			if (_needsUpdateItems)
			{
				_needsUpdateItems = false;
				return UpdateItems();
			}

			return false;
		}

		protected virtual bool UpdateItems()
		{
			if (ItemsPanelRoot != null && ShouldItemsControlManageChildren)
			{
				_needsUpdateItems = false;

				var items = GetItems() ?? Enumerable.Empty<object>();
				var containers = items
					.Cast<object>()
					.Select((_, index) =>
					{
						var container = GetContainerForIndex(index);
						PrepareContainerForIndex(container, index);
						return container;
					});

				var results = ItemsPanelRoot.Children.UpdateWithResults(containers.OfType<UIElement>(), comparer: new ViewComparer());

				foreach (var removed in results.Removed)
				{
					if (removed is DependencyObject removedObject)
					{
						CleanUpContainer(removedObject);
					}
				}

				return results.HasChanged();
			}

			return false;
		}

		protected virtual void ClearContainerForItemOverride(DependencyObject element, object item) { }

		internal virtual void ContainerClearedForItem(object item, SelectorItem itemContainer) { }

		/// <summary>
		/// Unset content of container. This should be called when the container is no longer going to be used.
		/// </summary>
		internal void CleanUpContainer(global::Windows.UI.Xaml.DependencyObject element)
		{
			object item;
			switch (element)
			{
				case ContentPresenter cp when cp is ContentPresenter:
					item = cp.Content;
					break;
				case ContentControl cc when cc is ContentControl:
					item = cc.Content;
					break;
				default:
					item = element;
					break;

			}
			ClearContainerForItemOverride(element, item);
			ContainerClearedForItem(item, element as SelectorItem);

			if (element is ContentPresenter presenter
				&& (
				presenter.ContentTemplate == ItemTemplate
				|| presenter.ContentTemplateSelector == ItemTemplateSelector
				)
			)
			{
				// Clear the Content property first, in order to avoid creating a text placeholder when
				// the ContentTemplate is removed.
				presenter.ClearValue(ContentPresenter.ContentProperty);

				presenter.ClearValue(ContentPresenter.ContentTemplateProperty);
				presenter.ClearValue(ContentPresenter.ContentTemplateSelectorProperty);
			}
			else if (element is ContentControl contentControl)
			{
				contentControl.ClearValue(DataContextProperty);
			}
		}

		private class ViewComparer : IEqualityComparer<View>
		{
			public bool Equals(View x, View y)
			{
				return x.Equals(y);
			}

			public int GetHashCode(View obj)
			{
				return obj.GetHashCode();
			}
		}

		/// <summary>
		/// Creates or identifies the element that is used to display the given item.
		/// </summary>
		/// <returns>The element that is used to display the given item.</returns>
		protected virtual DependencyObject GetContainerForItemOverride()
		{
			return InnerContentPresenterTemplate.LoadContentCached() as ContentPresenter;
		}

		/// <summary>
		/// Prepares the specified element to display the specified item.
		/// </summary>
		/// <param name="element">The element that's used to display the specified item.</param>
		/// <param name="item">The item to display.</param>
		protected virtual void PrepareContainerForItemOverride(DependencyObject element, object item)
		{
			var isOwnContainer = ReferenceEquals(element, item);

			var styleFromItemsControl = ItemContainerStyle ?? ItemContainerStyleSelector?.SelectStyle(item, element);

			object GetContent()
			{
				var displayMemberPath = DisplayMemberPath;
				if (string.IsNullOrEmpty(displayMemberPath))
				{
					return item;
				}
				else
				{
					// TODO: Cache the BindingPath
					var b = new BindingPath(displayMemberPath, item) { DataContext = item };
					return b.Value;
				}
			}

			//Prepare ContentPresenter
			if (element is ContentPresenter containerAsContentPresenter)
			{
				if (styleFromItemsControl != null)
				{
					containerAsContentPresenter.Style = styleFromItemsControl;
				}
				else
				{
					containerAsContentPresenter.Style = null;
				}

				containerAsContentPresenter.ContentTemplate = ItemTemplate;
				containerAsContentPresenter.ContentTemplateSelector = ItemTemplateSelector;

				if (!isOwnContainer)
				{
					containerAsContentPresenter.Content = GetContent();
				}
			}
			else if (element is ContentControl containerAsContentControl)
			{
				if (styleFromItemsControl != null)
				{
					containerAsContentControl.Style = styleFromItemsControl;
				}

				if (!containerAsContentControl.IsContainerFromTemplateRoot)
				{
					containerAsContentControl.ContentTemplate = ItemTemplate;
					containerAsContentControl.ContentTemplateSelector = ItemTemplateSelector;
				}

				if (!isOwnContainer)
				{
					TryRepairContentConnection(containerAsContentControl, item);

					// Set the datacontext first, then the binding.
					// This avoids the inner content to go through a partial content being
					// the result of the fallback value of the binding set below.
					containerAsContentControl.DataContext = GetContent();

					if (!containerAsContentControl.IsContainerFromTemplateRoot && containerAsContentControl.GetBindingExpression(ContentControl.ContentProperty) == null)
					{
						containerAsContentControl.SetBinding(ContentControl.ContentProperty, new Binding());
					}
				}
			}
		}

		/// <summary>
		/// Ensure the container template updates correctly when recycling with the same item.
		/// </summary>
		/// <remarks>
		/// This addresses the very specific case where 1) the item is a view, 2) It's being rebound to a container that was most
		/// recently used to show that same view, but 3) the view is no longer connected to the container, perhaps because it was bound to
		/// a different container in the interim.
		///
		/// To force the item view to be reconnected, we set DataContext to null, so that when we set it to the item view immediately afterward,
		/// it does something instead of nothing.
		/// </remarks>
		private void TryRepairContentConnection(ContentControl container, object item)
		{
			if (item is View itemView && container.DataContext == itemView && itemView.GetVisualTreeParent() == null)
			{
				container.DataContext = null;
			}
		}

		/// <summary>
		/// Determines whether the specified item is (or is eligible to be) its own container.
		/// </summary>
		/// <param name="item">The item to check.</param>
		/// <returns>true if the item is (or is eligible to be) its own container; otherwise, false.</returns>
		protected virtual bool IsItemItsOwnContainerOverride(object item)
		{
			return item is IFrameworkElement;
		}

		internal void PrepareContainerForIndex(DependencyObject container, int index)
		{
			_containerBeingPrepared = container;

			// This must be set before calling PrepareContainerForItemOverride
			container.SetValue(IndexForItemContainerProperty, index);

			var item = ItemFromIndex(index);
			PrepareContainerForItemOverride(container, item);
			ContainerPreparedForItem(item, container as SelectorItem, index);

			_containerBeingPrepared = null;
		}

		internal virtual void ContainerPreparedForItem(object item, SelectorItem itemContainer, int itemIndex)
		{
		}

		/// <summary>
		/// Create a new container for item at <paramref name="index"/> and returns it, unless the item is its own container, in which case
		/// returns the item itself, or the template root is a suitable container, in which case the template root is returned.
		/// </summary>
		/// <remarks>For virtualized controls, this method is only called if it's not possible to recycle an existing container.</remarks>
		internal DependencyObject GetContainerForIndex(int index)
		{
			var item = ItemFromIndex(index);

			if (IsItemItsOwnContainerOverride(item))
			{
				var container = item as DependencyObject;
				container.SetValue(ItemsControlForItemContainerProperty, new WeakReference<ItemsControl>(this));

				return container;
			}
			else
			{
				var template = DataTemplateHelper.ResolveTemplate(ItemTemplate, ItemTemplateSelector, item, null);
				return GetContainerForTemplate(template);
			}
		}

		/// <summary>
		/// Gets an appropriate container for a given template (either a new container, or the template's root if it is a container)
		/// </summary>
		/// <remarks>
		/// This is called directly by NativeListViewBase on Android rather than <see cref="GetContainerForIndex(int)"/>, since the recycling
		/// mechanism doesn't permit for the exact item index to be known when the container is created.
		/// </remarks>
		internal DependencyObject GetContainerForTemplate(DataTemplate template)
		{
			var container = GetRootOfItemTemplateAsContainer(template)
				?? GetContainerForItemOverride();

			container.SetValue(ItemsControlForItemContainerProperty, new WeakReference<ItemsControl>(this));

			return container;
		}

		private protected virtual DependencyObject GetRootOfItemTemplateAsContainer(DataTemplate template) => null;

		public object ItemFromContainer(DependencyObject container)
		{
			var index = IndexFromContainer(container);

			if (index > -1)
			{
				var item = ItemFromIndex(index);
				return item;
			}
			else
			{
				// If the container is actually an item, we can return itself
				if (Items.Contains(container))
				{
					return container;
				}
			}

			return null;
		}

		public DependencyObject ContainerFromItem(object item)
		{
			if (IsItemItsOwnContainer(item))
			{
				// Verify whether the item is actually present.
				var itemIndex = Items.IndexOf(item);
				if (itemIndex < 0)
				{
					return null;
				}

				return item as DependencyObject;
			}

			var index = IndexFromItem(item);
			return index == -1 ? null : MaterializedContainers.FirstOrDefault(materializedContainer => Equals(IndexFromContainer(materializedContainer), index));
		}

		public int IndexFromContainer(DependencyObject container)
		{
			var index = IndexFromContainerInner(container);
			if (index < 0)
			{
				// If the container is actually an item, we can return its index
				return Items.IndexOf(container);
			}

			if (_inProgressVectorChange != null)
			{
				if (_inProgressVectorChange.CollectionChange == CollectionChange.ItemRemoved)
				{
					if (index == _inProgressVectorChange.Index)
					{
						// Removed item no longer exists.
						return -1;
					}
					else if (index > _inProgressVectorChange.Index)
					{
						// All items after the removed item have a lower new index.
						return index - 1;
					}
				}
				else if (_inProgressVectorChange.CollectionChange == CollectionChange.ItemInserted)
				{
					if (index >= _inProgressVectorChange.Index)
					{
						// All items after the added item have a higher new index.
						return index + 1;
					}
				}
				else if (
					(_inProgressVectorChange.CollectionChange == CollectionChange.ItemChanged && _inProgressVectorChange.Index == index) ||
					_inProgressVectorChange.CollectionChange == CollectionChange.Reset)
				{
					// In these cases, we return the index only if the item is its own container
					// and the container is in fact the new item, not the old one					
					var item = ItemFromIndex(index);
					if (IsItemItsOwnContainer(item) && Equals(item, container))
					{
						return index;
					}
					return -1;
				}
			}
			return index;
		}

		internal virtual int IndexFromContainerInner(DependencyObject container)
		{
			return (int)container.GetValue(IndexForItemContainerProperty);
		}

		public DependencyObject ContainerFromIndex(int index)
		{
			var item = ItemFromIndex(index);
			if (IsItemItsOwnContainer(item))
			{
				return item as DependencyObject;
			}

			int adjustedIndex = GetInProgressAdjustedIndex(index);

			if (adjustedIndex < 0)
			{
				return null;
			}

			return ContainerFromIndexInner(adjustedIndex);
		}

		internal virtual DependencyObject ContainerFromIndexInner(int index)
		{
			return MaterializedContainers.FirstOrDefault(materializedContainer => Equals(materializedContainer.GetValue(IndexForItemContainerProperty), index));
		}

		protected int GetInProgressAdjustedIndex(int index)
		{
			int adjustedIndex = index;

			if (_inProgressVectorChange != null)
			{
				if (_inProgressVectorChange.CollectionChange == CollectionChange.ItemRemoved)
				{
					if (index >= _inProgressVectorChange.Index)
					{
						// All items after the removed item have still a higher index.
						adjustedIndex = index + 1;
					}
				}
				else if (_inProgressVectorChange.CollectionChange == CollectionChange.ItemInserted)
				{
					if (index == _inProgressVectorChange.Index)
					{
						return -1;
					}
					else if (index > _inProgressVectorChange.Index)
					{
						// All items after the added item have still a lower index.
						adjustedIndex = index - 1;
					}
				}
				else if (
					(_inProgressVectorChange.CollectionChange == CollectionChange.ItemChanged && _inProgressVectorChange.Index == index) ||
					_inProgressVectorChange.CollectionChange == CollectionChange.Reset)
				{
					// In case the item is not its own container, the new one is not assigned
					// yet and we return null.						
					adjustedIndex = -1;
				}
			}

			return adjustedIndex;
		}

		/// <summary>
		/// Returns the item for a given flat index.
		/// </summary>
		internal object ItemFromIndex(int index)
		{
			var items = GetItems();
			if (items != null && index >= 0 && index < NumberOfItems)
			{
				return items.ElementAt(index);
			}
			else
			{
				return null;
			}
		}

		internal int IndexFromItem(object item)
		{
			return GetItems()?.IndexOf(item) ?? -1;
		}

		internal bool IsItemItsOwnContainer(object item)
		{
			return IsItemItsOwnContainerOverride(item);
		}

		internal bool IsIndexItsOwnContainer(int index)
		{
			return IsItemItsOwnContainer(ItemFromIndex(index));
		}

		/// <summary>
		/// Return control which acts as container for group header for this ItemsControl subtype.
		/// </summary>
		internal virtual ContentControl GetGroupHeaderContainer(object groupHeader)
		{
			return ContentControl.CreateItemContainer();
		}

		public static ItemsControl ItemsControlFromItemContainer(DependencyObject container)
		{
			return (container.GetValue(ItemsControlForItemContainerProperty) as WeakReference<ItemsControl>)?.GetTarget();
		}

		internal IEnumerable<DependencyObject> MaterializedContainers =>
			GetItemsPanelChildren()
#if !NET461 // TODO
				.Prepend(_containerBeingPrepared) // we put it first, because it's the most likely to be requested
#endif
				.Trim()
				.Distinct();

		internal protected virtual IEnumerable<DependencyObject> GetItemsPanelChildren()
		{
			return ItemsPanelRoot?.Children.OfType<DependencyObject>() ?? Enumerable.Empty<DependencyObject>();
		}

		internal object GetDisplayItemFromIndexPath(Uno.UI.IndexPath indexPath)
		{
			if (!IsGrouping)
			{
				return GetItemFromIndex(indexPath.Row);
			}

			return GetGroupAtDisplaySection(indexPath.Section)?.GroupItems?[indexPath.Row];
		}

		/// <summary>
		/// Return the item in the source collection at a given item index.
		/// </summary>
		internal object GetItemFromIndex(int index)
		{
			var items = GetItems();
			if (items is IList<object> list)
			{
				// This is primarily an optimization for ICollectionView, which implements IList<object> but might not implement non-generic IList
				return list[index];
			}

			return items?.ElementAtOrDefault(index);
		}

		internal Uno.UI.IndexPath GetIndexPathFromItem(object item)
		{
			var unwrappedSource = UnwrapItemsSource();

			if (IsGrouping)
			{
				return (unwrappedSource as ICollectionView).GetIndexPathForItem(item);
			}

			var index = GetItems()?.IndexOf(item) ?? -1;

			return Uno.UI.IndexPath.FromRowSection(index, 0);
		}

		internal ICollectionViewGroup GetGroupAtDisplaySection(int displaySection)
		{
			if (!AreEmptyGroupsHidden)
			{
				return GetGroupAt(displaySection);
			}

			return CollectionGroups.Where(g => (g as ICollectionViewGroup).GroupItems.Count > 0)
				.ElementAt(displaySection) as ICollectionViewGroup;
		}

		internal ICollectionViewGroup GetGroupAt(int section) => CollectionGroups[section] as ICollectionViewGroup;

		/// <summary>
		/// Sets the ItemsPresenter that should be used by ItemsControl.
		/// </summary>
		/// <remarks>
		/// This is usually called from ItemsPresenter when its TemplatedParent (an ItemsControl) gets set.
		/// </remarks>
		internal void SetItemsPresenter(ItemsPresenter itemsPresenter)
		{
			if (_itemsPresenter != itemsPresenter)
			{
				_itemsPresenter = itemsPresenter;
				_itemsPresenter?.SetItemsPanel(InternalItemsPanelRoot);
			}
		}

		internal DataTemplate ResolveItemTemplate(object item)
		{
			return DataTemplateHelper.ResolveTemplate(ItemTemplate, ItemTemplateSelector, item, this);
		}

		internal protected virtual void CleanUpInternalItemsPanel(View panel) { }

		/// <summary>
		/// Resets internal cached state of the collection.
		/// </summary>
		private protected virtual void Refresh() { }
	}
}
