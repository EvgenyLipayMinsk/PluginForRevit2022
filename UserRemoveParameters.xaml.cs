using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Collections;
using MS.Internal.Controls;
using System.Xml.Linq;

namespace PluginForRevit2022
{
    /// <summary>
    /// Логика взаимодействия для UserRemoveParameters.xaml
    /// </summary>
    public partial class UserRemoveParameters : Window
    {
        ICollection<Element> allParameters = null;
        
        Autodesk.Revit.DB.Document doc;
        public UserRemoveParameters(ICollection<Element> elements, Autodesk.Revit.DB.Document doc)
        {
            InitializeComponent();
            allParameters = elements;
           
            foreach (var parameter in allParameters)
            {
                //System.Windows.Controls.ListBox checkParam = new System.Windows.Controls.ListBox();
                //checkParam.DisplayMemberPath = parameter.Name;
                //RemoveParametersList.Children.Add(checkParam);
                ParametersListBox.DisplayMemberPath = parameter.Name;
            }

            //RemoveParametersList.DisplayMemberPath = "Name";
        }
        private void removeParametersButton(object sender, RoutedEventArgs e)
        {
            ICollection<Element> parametersForRemove = null;
            ICollection<Element> allCheckedParameters = null;

            if (ParametersListBox.SelectedItems != null)
            {
                allCheckedParameters = (ICollection<Element>)ParametersListBox.SelectedItems;
            }

            //UIElementCollection comboBoxes = RemoveParametersList.Children;
            //List<String> allCheckedParameters = new List<string>();
            //foreach (UIElement element1 in comboBoxes)
            //{


            //    //System.Windows.Controls.CheckBox comboBox = element as System.Windows.Controls.CheckBox;
            //    System.Windows.Controls.CheckBox comboBox = element1 as System.Windows.Controls.CheckBox;
            //    if (comboBox.IsChecked == true)
            //        allCheckedParameters.Add(comboBox.Content.ToString());
            //}
            foreach (Element element1 in allParameters)
            {
                string parameterName = element1.Name;
                if (allCheckedParameters.Contains(element1))
                {
                    parametersForRemove.Add(element1);
                }
            }
           
            RemoveParameters(parametersForRemove,doc);
        }
        public void RemoveParameters(ICollection<Element> parameters, Autodesk.Revit.DB.Document doc)
        {
            int count = 0;
            using (Transaction t = new Transaction(doc, "Delete Parameter"))
            {
                // start a transaction within the valid Revit API context
                t.Start();
                foreach (Element param in parameters)
                {
                    doc.Delete((ICollection<ElementId>)param);
                    count++;
                }
                t.Commit();
                t.Dispose();
            }
            string cmdRemoveInfo = $"Remove {count} parameters";
            System.Windows.Forms.MessageBox.Show($"'{cmdRemoveInfo}'", "Resave families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }



        private void Button_Click_Cancel(object sender, RoutedEventArgs e)
        {
            
        }
    }
}
