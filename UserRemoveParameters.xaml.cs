using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using MS.Internal.Controls;
using System.Xml.Linq;
using System.Linq;

namespace PluginForRevit2022
{
    /// <summary>
    /// Логика взаимодействия для UserRemoveParameters.xaml
    /// </summary>
    public partial class UserRemoveParameters : Window
    {
        
        Autodesk.Revit.DB.Document m_doc;
        public UserRemoveParameters(List<Element> elements, Autodesk.Revit.DB.Document doc)
        {
            InitializeComponent();

            m_doc = doc;
            
            listBox.ItemsSource = elements;
        }
        private void removeParametersButton(object sender, RoutedEventArgs e)
        {
            List<Element> deletedParameters = listBox.SelectedItems.Cast<Element>().ToList();
     
            RemoveParameters(deletedParameters);
        }

        public void RemoveParameters(List<Element> deletedParameters)
        {
            
            using (Transaction t = new Transaction(m_doc, "Delete Parameter"))
            {
                // start a transaction within the valid Revit API context
                t.Start();
                List<ElementId> deletedParameterIds = deletedParameters.Select(e => e.Id).ToList();
                m_doc.Delete(deletedParameterIds);
            
                t.Commit();
                t.Dispose();
            }
        }

        private void Button_Click_Cancel(object sender, RoutedEventArgs e)
        {
            
        }

    }
}
