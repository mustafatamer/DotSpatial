﻿// Copyright (c) DotSpatial Team. All rights reserved.
// Licensed under the MIT license. See License.txt file in the project root for full license information.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DotSpatial.Controls;
using DotSpatial.Controls.Header;
using DotSpatial.Data;
using DotSpatial.Plugins.WebMap.Properties;
using DotSpatial.Plugins.WebMap.Tiling;
using DotSpatial.Projections;
using DotSpatial.Symbology;
using GeoAPI.Geometries;

namespace DotSpatial.Plugins.WebMap
{
    /// <summary>
    /// The WebMapPlugin can be used to load web based layers.
    /// </summary>
    public class WebMapPlugin : Extension
    {
        #region Fields

        private const string Other = "Other";
        private const string PluginName = "FetchBasemap";
        private const string StrKeyOpacityDropDown = "kOpacityDropDown";
        private const string StrKeyServiceDropDown = "kServiceDropDown";

        private readonly ProjectionInfo _webMercProj = ProjectionInfo.FromEsriString(KnownCoordinateSystems.Projected.World.WebMercator.ToEsriString());
        private readonly ProjectionInfo _wgs84Proj = ProjectionInfo.FromEsriString(KnownCoordinateSystems.Geographic.World.WGS1984.ToEsriString());

        private MapImageLayer _baseMapLayer;
        private bool _busySet;
        private BackgroundWorker _bw;
        private ServiceProvider _emptyProvider;
        private int _extendBufferCoeff = 3;
        private IMapFeatureLayer _featureSetLayer;
        private short _opacity = 100;

        private DropDownActionItem _opacityDropDown;
        private SimpleActionItem _optionsAction;
        private DropDownActionItem _serviceDropDown;
        private TileManager _tileManager;

        #endregion

        #region  Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="WebMapPlugin"/> class.
        /// </summary>
        public WebMapPlugin()
        {
            DeactivationAllowed = false;
            _busySet = false;
        }

        #endregion

        #region Properties

        private ServiceProvider CurrentProvider => (ServiceProvider)_serviceDropDown.SelectedItem;

        #endregion

        #region Methods

        /// <summary>
        /// Initialize the DotSpatial plugin
        /// </summary>
        public override void Activate()
        {
            // Add Menu or Ribbon buttons.
            AddServiceDropDown(App.HeaderControl);

            _optionsAction = new SimpleActionItem("Configure", (sender, args) =>
                                 {
                                     var p = CurrentProvider;
                                     if (p == null) return;
                                     var cf = p.Configure;
                                     if (cf != null)
                                     {
                                         if (cf())
                                         {
                                             // Update map if configuration changed
                                             EnableBasemapFetching(p);
                                         }
                                     }
                                 })
                                 {
                                     Key = "kOptions",
                                     RootKey = HeaderControl.HomeRootItemKey,
                                     GroupCaption = Resources.Panel_Name,
                                     Enabled = false,
                                 };
            App.HeaderControl.Add(_optionsAction);
            AddOpaticyDropDown(App.HeaderControl);

            _serviceDropDown.SelectedValueChanged += ServiceSelected;
            _serviceDropDown.SelectedItem = _emptyProvider;

            // Add handlers for saving/restoring settings
            App.SerializationManager.Serializing += SerializationManagerSerializing;
            App.SerializationManager.Deserializing += SerializationManagerDeserializing;
            App.SerializationManager.NewProjectCreated += SerializationManagerNewProject;

            // Setup the background worker
            _bw = new BackgroundWorker
                      {
                          WorkerSupportsCancellation = true,
                          WorkerReportsProgress = true
                      };
            _bw.DoWork += BwDoWork;
            _bw.RunWorkerCompleted += BwRunWorkerCompleted;
            _bw.ProgressChanged += BwProgressChanged;

            base.Activate();
        }

        /// <summary>
        /// Fires when the plugin should become inactive
        /// </summary>
        public override void Deactivate()
        {
            App.HeaderControl.RemoveAll();

            DisableBasemapLayer();

            // remove handlers for saving/restoring settings
            App.SerializationManager.Serializing -= SerializationManagerSerializing;
            App.SerializationManager.Deserializing -= SerializationManagerDeserializing;
            App.SerializationManager.NewProjectCreated -= SerializationManagerNewProject;

            base.Deactivate();
        }

        private void AddBasemapLayerToMap()
        {
            if (!InsertBaseMapLayer(App.Map.MapFrame))
            {
                App.Map.Layers.Add(_baseMapLayer);
            }
        }

        private void AddOpaticyDropDown(IHeaderControl header)
        {
            string defaultOpacity = null;
            _opacityDropDown = new DropDownActionItem
                                   {
                                       AllowEditingText = true,
                                       Caption = Resources.Opacity_Box_Text,
                                       ToolTipText = Resources.Opacity_Box_ToolTip,
                                       Width = 45,
                                       Key = StrKeyOpacityDropDown
                                   };

            // Make some opacity settings
            for (var i = 100; i > 0; i -= 10)
            {
                var opacity = i.ToString(CultureInfo.InvariantCulture);
                if (i == 100)
                {
                    defaultOpacity = opacity;
                }

                _opacityDropDown.Items.Add(opacity);
            }

            _opacityDropDown.GroupCaption = Resources.Panel_Name;
            _opacityDropDown.SelectedValueChanged += OpacitySelected;
            _opacityDropDown.RootKey = HeaderControl.HomeRootItemKey;

            // Add it to the Header
            header.Add(_opacityDropDown);
            if (defaultOpacity != null)
                _opacityDropDown.SelectedItem = defaultOpacity;
        }

        private void AddServiceDropDown(IHeaderControl header)
        {
            _serviceDropDown = new DropDownActionItem
                                   {
                                       Key = StrKeyServiceDropDown,
                                       RootKey = HeaderControl.HomeRootItemKey,
                                       Width = 145,
                                       AllowEditingText = false,
                                       ToolTipText = Resources.Service_Box_ToolTip,
                                       GroupCaption = Resources.Panel_Name
                                   };

            // "None" provider
            _emptyProvider = new ServiceProvider(Resources.None);
            _serviceDropDown.Items.Add(_emptyProvider);

            // Default providers
            _serviceDropDown.Items.AddRange(ServiceProviderFactory.GetDefaultServiceProviders());

            // "Other" provider
            _serviceDropDown.Items.Add(ServiceProviderFactory.Create(Other));

            // Add it to the Header
            header.Add(_serviceDropDown);
        }

        private void BaseMapLayerRemoveItem(object sender, EventArgs e)
        {
            if (_baseMapLayer != null)
            {
                _baseMapLayer.RemoveItem -= BaseMapLayerRemoveItem;
            }

            _baseMapLayer = null;
            _serviceDropDown.SelectedItem = _emptyProvider;
            App.Map.MapFrame.ViewExtentsChanged -= MapFrameExtentsChanged;
        }

        private void BwDoWork(object sender, DoWorkEventArgs e)
        {
            var worker = sender as BackgroundWorker;
            if (App.Map != null && !_busySet)
            {
                _busySet = true;
                App.Map.IsBusy = true;
            }

            if (worker != null && _baseMapLayer != null)
            {
                if (worker.CancellationPending)
                {
                    e.Cancel = true;
                }
                else
                {
                    worker.ReportProgress(10);
                    UpdateStichedBasemap(e);
                }
            }
        }

        private void BwProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Do we know what what our progress completion percent is (instead of 50)?
            App.ProgressHandler.Progress(e.ProgressPercentage, "Loading Basemap ...");
        }

        private void BwRunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                _bw.RunWorkerAsync();
                return;
            }

            if (App.Map != null)
            {
                App.Map.IsBusy = false;
                _busySet = false;
                App.Map.MapFrame.Invalidate();
                App.ProgressHandler.Reset();
            }
        }

        /// <summary>
        /// Changes the opacity of the current basemap image and invalidates the map.
        /// </summary>
        private void ChangeBasemapOpacity()
        {
            MapFrameExtentsChanged(this, new ExtentArgs(App.Map.ViewExtents)); // this forces the map layer to refresh
        }

        private void DisableBasemapLayer()
        {
            RemoveBasemapLayer(_baseMapLayer);
            RemoveBasemapLayer(_featureSetLayer);

            _optionsAction.Enabled = false;
            _baseMapLayer = null;
            _featureSetLayer = null;

            App.Map.MapFrame.ViewExtentsChanged -= MapFrameExtentsChanged;
        }

        private void EnableBasemapFetching(ServiceProvider provider)
        {
            // Zoom out as much as possible if there are no other layers.
            // reproject any existing layer in non-webMercator projection.
            if (App.Map.Layers.Count == 0)
            {
                ForceMaxExtentZoom();
            }
            else if (!App.Map.Projection.Equals(_webMercProj))
            {
                // get original view extents
                App.ProgressHandler.Progress(0, "Reprojecting Map Layers...");
                double[] viewExtentXy = { App.Map.ViewExtents.MinX, App.Map.ViewExtents.MinY, App.Map.ViewExtents.MaxX, App.Map.ViewExtents.MaxY };
                double[] viewExtentZ = { 0.0, 0.0 };

                // reproject view extents
                Reproject.ReprojectPoints(viewExtentXy, viewExtentZ, App.Map.Projection, _webMercProj, 0, 2);

                // set the new map extents
                App.Map.ViewExtents = new Extent(viewExtentXy);

                // if projection is not WebMercator - reproject all layers:
                App.Map.MapFrame.ReprojectMapFrame(_webMercProj);

                App.ProgressHandler.Progress(0, "Loading Basemap...");
            }

            EnableBasemapLayer();
            _tileManager = new TileManager(provider);
            RunOrCancelBw();
        }

        private void EnableBasemapLayer()
        {
            if (_baseMapLayer == null)
            {
                // Need to first initialize and add the basemap layer synchronously (it will fail if done in another thread).

                // First create a temporary imageData with an Envelope (otherwise adding to the map will fail)
                var tempImageData = new InRamImageData(Resources.nodata, new Extent(1, 1, 2, 2));

                _baseMapLayer = new MapImageLayer(tempImageData)
                                    {
                                        Projection = App.Map.Projection,
                                        LegendText = Resources.Legend_Title
                                    };

                _baseMapLayer.RemoveItem += BaseMapLayerRemoveItem;
                AddBasemapLayerToMap();
            }

            App.Map.MapFrame.ViewExtentsChanged -= MapFrameExtentsChanged;
            App.Map.MapFrame.ViewExtentsChanged += MapFrameExtentsChanged;
        }

        private void ForceMaxExtentZoom()
        {
            // special case when there are no other layers in the map. Set map projection to WebMercator and zoom to max ext.
            App.Map.MapFrame.ReprojectMapFrame(_webMercProj);

            // modifying the view extents didn't get the job done, so we are creating a new featureset.
            var fs = new FeatureSet(FeatureType.Point);
            fs.Features.Add(new Coordinate(TileCalculator.MinWebMercX, TileCalculator.MinWebMercY));
            fs.Features.Add(new Coordinate(TileCalculator.MaxWebMercX, TileCalculator.MaxWebMercY));

            fs.Projection = App.Map.Projection;
            _featureSetLayer = App.Map.Layers.Add(fs);

            // hide the points that we are adding.
            _featureSetLayer.LegendItemVisible = false;

            App.Map.ZoomToMaxExtent();
        }

        private bool InsertBaseMapLayer(IMapGroup group)
        {
            for (var i = 0; i < group.Layers.Count; i++)
            {
                var layer = group.Layers[i];
                var childGroup = layer as IMapGroup;
                if (childGroup != null)
                {
                    var ins = InsertBaseMapLayer(childGroup);
                    if (ins) return true;
                }

                if (layer is IMapPointLayer || layer is IMapLineLayer)
                {
                    var grp = layer.GetParentItem() as IGroup;
                    if (grp != null)
                    {
                        grp.Insert(i, _baseMapLayer);
                        return true;
                    }
                }
            }

            return false;
        }

        private void MapFrameExtentsChanged(object sender, ExtentArgs e)
        {
            RunOrCancelBw();
        }

        /// <summary>
        /// Changes the Maps opacity to the selected opacity.
        /// </summary>
        /// <param name="sender">Sender that raised the event.</param>
        /// <param name="e">The event args.</param>
        private void OpacitySelected(object sender, SelectedValueChangedEventArgs e)
        {
            if (_baseMapLayer == null)
                return;

            short opacityInt;

            // Check to make sure the text in the box is an integer and we are in the range
            if (!short.TryParse(e.SelectedItem as string, out opacityInt) || opacityInt > 100 || opacityInt < 0)
            {
                opacityInt = 100;
                _opacityDropDown.SelectedItem = opacityInt;
            }

            _opacity = opacityInt;
            ChangeBasemapOpacity();
        }

        /// <summary>
        /// Finds and removes the online basemap layer.
        /// </summary>
        /// <param name="layer">Name of the online basemap layer.</param>
        private void RemoveBasemapLayer(IMapLayer layer)
        {
            // attempt to remove from the top-level
            if (App.Map.Layers.Remove(layer)) return;

            // Remove from other groups if the user has moved it
            foreach (var group in App.Map.Layers.OfType<IMapGroup>())
            {
                group.Remove(layer);
            }
        }

        private void RunOrCancelBw()
        {
            if (!_bw.IsBusy)
                _bw.RunWorkerAsync();
            else
                _bw.CancelAsync();
        }

        /// <summary>
        /// Deserializes the WebMap settings and loads the corresponding basemap.
        /// </summary>
        /// <param name="sender">Sender that raised the event.</param>
        /// <param name="e">The event args.</param>
        private void SerializationManagerDeserializing(object sender, SerializingEventArgs e)
        {
            try
            {
                if (_baseMapLayer != null)
                {
                    // disable BaseMap because we might be loading a project that doesn't have a basemap
                    DisableBasemapLayer();
                    _serviceDropDown.SelectedItem = _emptyProvider;
                }

                // Set opacity
                var opacity = App.SerializationManager.GetCustomSetting(PluginName + "_Opacity", "100");
                _opacityDropDown.SelectedItem = opacity;
                _opacity = Convert.ToInt16(opacity);

                var basemapName = App.SerializationManager.GetCustomSetting(PluginName + "_BasemapName", Resources.None);
                if (basemapName.Equals(Resources.None))
                {
                    // make sure there is no basemap layer that shouldn't be there
                    var tmpLayer = (MapImageLayer)App.Map.MapFrame.GetAllLayers().FirstOrDefault(layer => layer.LegendText == Resources.Legend_Title);
                    if (tmpLayer != null)
                        RemoveBasemapLayer(tmpLayer);
                }
                else
                {
                    _baseMapLayer = (MapImageLayer)App.Map.MapFrame.GetAllLayers().FirstOrDefault(layer => layer.LegendText == Resources.Legend_Title);
                    if (_baseMapLayer != null) _baseMapLayer.Projection = _webMercProj; // changed by jany_(2015-07-09) set the projection because if it is not set we produce a cross thread exception when DotSpatial tries to show the projection dialog

                    // hack: need to set provider to original object, not a new one.
                    _serviceDropDown.SelectedItem = _serviceDropDown.Items.OfType<ServiceProvider>().FirstOrDefault(p => p.Name.Equals(basemapName, StringComparison.InvariantCultureIgnoreCase));
                    var pp = CurrentProvider;
                    if (pp != null)
                    {
                        EnableBasemapFetching(pp);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Print(ex.StackTrace);
            }
        }

        /// <summary>
        /// Deactivates the WebMap when a new project is created.
        /// </summary>
        /// <param name="sender">Sender that raised the event.</param>
        /// <param name="e">The event args.</param>
        private void SerializationManagerNewProject(object sender, SerializingEventArgs e)
        {
            if (_baseMapLayer == null) return;
            DisableBasemapLayer();
            _serviceDropDown.SelectedItem = _emptyProvider;
        }

        /// <summary>
        /// Serializes the WebMap settings.
        /// </summary>
        /// <param name="sender">Sender that raised the event.</param>
        /// <param name="e">The event args.</param>
        private void SerializationManagerSerializing(object sender, SerializingEventArgs e)
        {
            var p = CurrentProvider;
            App.SerializationManager.SetCustomSetting(PluginName + "_BasemapName", p != null ? p.Name : Resources.None);
            App.SerializationManager.SetCustomSetting(PluginName + "_Opacity", _opacity.ToString(CultureInfo.InvariantCulture));
        }

        private void ServiceSelected(object sender, SelectedValueChangedEventArgs e)
        {
            var p = CurrentProvider;
            if (p == null || p.Name == Resources.None)
            {
                DisableBasemapLayer();
            }
            else
            {
                _optionsAction.Enabled = p.Configure != null;
                if (p.NeedConfigure)
                {
                    p.Configure?.Invoke();
                }

                EnableBasemapFetching(p);
            }
        }

        /// <summary>
        /// Main method of this plugin: gets the tiles from the TileManager, stitches them together, and adds the layer to the map.
        /// </summary>
        /// <param name="e">The event args.</param>
        private void UpdateStichedBasemap(DoWorkEventArgs e)
        {
            var map = App.Map as Map;
            if (map == null) return;

            var bwProgress = (Func<int, bool>)(p =>
                {
                    _bw.ReportProgress(p);
                    if (_bw.CancellationPending)
                    {
                        e.Cancel = true;
                        return false;
                    }

                    return true;
                });

            var rectangle = map.Bounds;
            var webMercExtent = map.ViewExtents.Clone() as Extent;

            if (webMercExtent == null) return;

            // If ExtendBuffer, correct the displayed extent
            if (map.ExtendBuffer)
                webMercExtent.ExpandBy(-webMercExtent.Width / _extendBufferCoeff, -webMercExtent.Height / _extendBufferCoeff);

            // Clip the reported Web Merc Envelope to be within possible Web Merc extents
            //  This fixes an issue with Reproject returning bad results for very large (impossible) web merc extents reported from the Map
            var webMercTopLeftX = TileCalculator.Clip(webMercExtent.MinX, TileCalculator.MinWebMercX, TileCalculator.MaxWebMercX);
            var webMercTopLeftY = TileCalculator.Clip(webMercExtent.MaxY, TileCalculator.MinWebMercY, TileCalculator.MaxWebMercY);
            var webMercBtmRightX = TileCalculator.Clip(webMercExtent.MaxX, TileCalculator.MinWebMercX, TileCalculator.MaxWebMercX);
            var webMercBtmRightY = TileCalculator.Clip(webMercExtent.MinY, TileCalculator.MinWebMercY, TileCalculator.MaxWebMercY);

            if (!bwProgress(25)) return;

            // Get the web mercator vertices of the current map view
            var mapVertices = new[] { webMercTopLeftX, webMercTopLeftY, webMercBtmRightX, webMercBtmRightY };
            double[] z = { 0, 0 };

            // Reproject from web mercator to WGS1984 geographic
            Reproject.ReprojectPoints(mapVertices, z, _webMercProj, _wgs84Proj, 0, mapVertices.Length / 2);
            var geogEnv = new Envelope(mapVertices[0], mapVertices[2], mapVertices[1], mapVertices[3]);

            if (!bwProgress(40)) return;

            // Grab the tiles
            var tiles = _tileManager.GetTiles(geogEnv, rectangle, _bw);
            if (!bwProgress(50)) return;

            // Stitch them into a single image
            var stitchedBasemap = TileCalculator.StitchTiles(tiles.Bitmaps, _opacity);
            var tileImage = new InRamImageData(stitchedBasemap)
                                {
                                    Projection = _baseMapLayer.Projection
                                };

            // report progress and check for cancel
            if (!bwProgress(70)) return;

            // Tiles will have often slightly different bounds from what we are displaying on screen
            // so we need to get the top left and bottom right tiles' bounds to get the proper extent
            // of the tiled image
            var tileVertices = new[] { tiles.TopLeftTile.MinX, tiles.TopLeftTile.MaxY, tiles.BottomRightTile.MaxX, tiles.BottomRightTile.MinY };

            // Reproject from WGS1984 geographic coordinates to web mercator so we can show on the map
            Reproject.ReprojectPoints(tileVertices, z, _wgs84Proj, _webMercProj, 0, tileVertices.Length / 2);

            tileImage.Bounds = new RasterBounds(stitchedBasemap.Height, stitchedBasemap.Width, new Extent(tileVertices[0], tileVertices[3], tileVertices[2], tileVertices[1]));

            // report progress and check for cancel
            if (!bwProgress(90)) return;

            _baseMapLayer.Image = tileImage;

            // report progress and check for cancel
            bwProgress(99);
        }

        #endregion
    }
}