using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ARBM.CableJournalExporter.Models
{
    /// <summary>
    /// Represents a unified abstraction of a device participating in cable‑chain
    /// formation for any engineering system (APS, Security, SKUD, SCS).
    /// <para>RU: Представляет унифицированную абстракцию устройства, участвующего
    /// в формировании кабельных цепей для любой инженерной системы
    /// (АПС, Охранная сигнализация, СКУД, СКС).</para>
    /// </summary>
    public class DeviceInfo
    {
        /// <summary>
        /// The underlying Revit element representing the device.
        /// <para>RU: Исходный элемент Revit, представляющий устройство.</para>
        /// </summary>
        public Element Element { get; set; }

        /// <summary>
        /// Human‑readable device name (e.g., detector, module, reader).
        /// <para>RU: Человекочитаемое имя устройства (например, извещатель, модуль, считыватель).</para>
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// System type to which this device belongs.
        /// <para>RU: Тип инженерной системы, к которой относится устройство.</para>
        /// </summary>
        public SystemType SystemType { get; set; }

        /// <summary>
        /// Optional identifier used for chain grouping or addressing.
        /// <para>RU: Необязательный идентификатор, используемый для группировки цепей или адресации.</para>
        /// </summary>
        public string Address { get; set; }

        /// <summary>
        /// Optional reference to the panel, controller, or module this device is connected to.
        /// <para>RU: Необязательная ссылка на прибор, контроллер или модуль,
        /// к которому подключено устройство.</para>
        /// </summary>
        public Element ParentElement { get; set; }

        /// <summary>
        /// Creates an empty device descriptor.
        /// <para>RU: Создаёт пустой дескриптор устройства.</para>
        /// </summary>
        public DeviceInfo()
        {
        }

        /// <summary>
        /// Creates a device descriptor from a Revit element.
        /// <para>RU: Создаёт дескриптор устройства на основе элемента Revit.</para>
        /// </summary>
        public DeviceInfo(Element element, SystemType systemType)
        {
            Element = element;
            SystemType = systemType;
            Name = element?.Name;
        }
    }   

   

    
}
