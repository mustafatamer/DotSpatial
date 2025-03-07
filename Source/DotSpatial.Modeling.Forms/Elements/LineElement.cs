﻿// Copyright (c) DotSpatial Team. All rights reserved.
// Licensed under the MIT license. See License.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using DotSpatial.Data;
using DotSpatial.Data.Forms;
using DotSpatial.Modeling.Forms.Parameters;

namespace DotSpatial.Modeling.Forms.Elements
{
    /// <summary>
    ///  Line Element for use in the tool dialog
    /// </summary>
    internal partial class LineElement : DialogElement
    {
        #region Fields
        private readonly List<DataSetArray> _dataSets;
        private DataSetArray _addedFeatureSet;
        private bool _refreshCombo = true;
        #endregion

        #region  Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="LineElement"/> class.
        /// </summary>
        /// <param name="inputParam">The parameter this element represents</param>
        /// <param name="dataSets">An array of available data</param>
        public LineElement(LineFeatureSetParam inputParam, List<DataSetArray> dataSets)
        {
            // Tasarımcı tarafından gerekli
            InitializeComponent();

            // Geçtiğimiz parametreleri kaydediyoruz
            Param = inputParam;

            _dataSets = dataSets;

            // Etiketi kaydeder
            GroupBox.Text = Param.Name;

            DoRefresh();
        }

        #endregion

        #region Methods

        /// <summary>
        /// bir şey değiştirildiyse parametreyi günceller
        /// </summary>
        public override void Refresh()
        {
            DoRefresh();
        }

        /// <summary>
        /// Adds a new entry to the drop down list from data provider
        /// </summary>
        /// <param name="sender">Sender that raised the event.</param>
        /// <param name="e">The event args.</param>
        private void BtnAddDataClick(object sender, EventArgs e)
        {
            // Replace with something that uses the default data provider
            IFeatureSet tempFeatureSet = DataManager.DefaultDataManager.OpenVector();

            // If the feature is null don't do anything the user probably hit cancel on the dialog
            if (tempFeatureSet == null)
                return;

            // Else if the wrong feature type is returned don't add it and indicate whats wrong
            if (tempFeatureSet.FeatureType != FeatureType.Line)
            {
                MessageBox.Show(ModelingMessageStrings.FeatureTypeException);
            }

            // If its good add the feature set and save it
            else
            {
                _addedFeatureSet = new DataSetArray(Path.GetFileNameWithoutExtension(tempFeatureSet.Filename), tempFeatureSet);
                Param.ModelName = _addedFeatureSet.Name;
                Param.Value = _addedFeatureSet.DataSet;
            }
        }

        /// <summary>
        /// This fires when the selected value in the combo box is changed
        /// </summary>
        /// <param name="sender">Sender that raised the event.</param>
        /// <param name="e">The event args.</param>
        private void ComboFeatureSelectedValueChanged(object sender, EventArgs e)
        {
            if (_refreshCombo)
            {
                DataSetArray dsa = comboFeatures.SelectedItem as DataSetArray;
                if (dsa != null)
                {
                    Param.ModelName = dsa.Name;
                    Param.Value = dsa.DataSet;
                }
            }
        }

        private void DoRefresh()
        {
            // Birleşik giriş kutusunu geçici olarak devre dışı bırakın
            _refreshCombo = false;

            // Başlamak için açılan kutuların durumunu boş olarak ayarladık
            Status = ToolStatus.Empty;
            LightTipText = ModelingMessageStrings.FeaturesetMissing;
            comboFeatures.Items.Clear();

            // If the user added a feature set
            if (_addedFeatureSet != null)
            {
                comboFeatures.Items.Add(_addedFeatureSet);
                if (Param.Value != null && Param.DefaultSpecified)
                {
                    if (_addedFeatureSet.DataSet == Param.Value)
                    {
                        comboFeatures.SelectedItem = _addedFeatureSet;
                        Status = ToolStatus.Ok;
                        LightTipText = ModelingMessageStrings.FeaturesetValid;
                    }
                }
            }

            // Add all the dataSets back to the combo box
            if (_dataSets != null)
            {
                foreach (DataSetArray dsa in _dataSets)
                {
                    IFeatureSet aFeatureSet = dsa.DataSet as IFeatureSet;

                    // If the featureset is the correct type and isn't already in the combo box we add it
                    if (aFeatureSet?.FeatureType == FeatureType.Line && !comboFeatures.Items.Contains(dsa))
                    {
                        comboFeatures.Items.Add(dsa);
                        if (Param.Value != null && Param.DefaultSpecified)
                        {
                            if (dsa.DataSet == Param.Value)
                            {
                                comboFeatures.SelectedItem = dsa;
                                Status = ToolStatus.Ok;
                                LightTipText = ModelingMessageStrings.FeaturesetValid;
                            }
                        }
                    }
                }
            }

            _refreshCombo = true;
        }

        #endregion
    }
}