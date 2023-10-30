using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Windows;
using System.Linq;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System;
using System.Windows.Forms;
using System.Text;

namespace PluginForRevit2022
{

    /// <summary>
    /// Логика взаимодействия для UserRemoveParameters.xaml
    /// </summary>
    public partial class UserRemoveParameters : Window
    {
        private ObservableCollection<Element> Parameters;
        
        Autodesk.Revit.DB.Document m_doc;
        public UserRemoveParameters(List<Element> elements, Autodesk.Revit.DB.Document doc)
        {
            InitializeComponent();

            // окно помещается в центре экрана
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Parameters = new ObservableCollection<Element>(elements);

            m_doc = doc;

            listBox.ItemsSource = Parameters;
            Parameters.CollectionChanged += Parameters_CollectionChanged;
        }

       
        private void removeParametersButton(object sender, RoutedEventArgs e)
        {
            List<Element> deletedParameters = listBox.SelectedItems.Cast<Element>().ToList();

            // Проверять присвоенно ли парамерам занчение в проекте?


            RemoveParameters(deletedParameters);
        }

        public void RemoveParameters(List<Element> deletedParameters)
        {
            var info = new StringBuilder("Delete Parameters:\n");
           
            foreach (Element deletedParameter in deletedParameters)
            {
                info.Append($"{deletedParameter.Name}\n");
                Parameters.Remove(deletedParameter);
                
            }

            using (Transaction t = new Transaction(m_doc, "Delete Parameter"))
            {
                // start a transaction within the valid Revit API context
                t.Start();
                List<ElementId> deletedParameterIds = deletedParameters.Select(e => e.Id).ToList();
                m_doc.Delete(deletedParameterIds);
            
                t.Commit();
                t.Dispose();
            }
           
            
            System.Windows.Forms.MessageBox.Show($"'{info}'", "Delete parameters command", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        // обработчик изменения коллекции
        void Parameters_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems?[0] is List<Element>)
            {
                listBox.ItemsSource = Parameters;
            }
        }
        private void Button_Click_Cancel(object sender, RoutedEventArgs e)
        {
            Close();
        }

      

        
        

    }

}
