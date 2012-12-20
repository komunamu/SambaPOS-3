﻿using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Samba.Domain.Models.Inventories;
using Samba.Domain.Models.Settings;
using Samba.Domain.Models.Tickets;
using Samba.Persistance.DaoClasses;
using Samba.Persistance.Data;
using Samba.Presentation.Services.Common;
using Samba.Services;

namespace Samba.Presentation.Services.Implementations.InventoryModule
{
    internal class SalesData
    {
        public string MenuItemName { get; set; }
        public int MenuItemId { get; set; }
        public string PortionName { get; set; }
        public decimal Total { get; set; }
    }

    [Export(typeof(IInventoryService))]
    public class InventoryService : IInventoryService
    {
        private readonly IInventoryDao _inventoryDao;
        private readonly IApplicationState _applicationState;
        private readonly ICacheService _cacheService;

        [ImportingConstructor]
        public InventoryService(IInventoryDao inventoryDao, IApplicationState applicationState, ICacheService cacheService)
        {
            _inventoryDao = inventoryDao;
            _applicationState = applicationState;
            _cacheService = cacheService;

            EventServiceFactory.EventService.GetEvent<GenericEvent<WorkPeriod>>().Subscribe(OnWorkperiodStatusChanged);
        }

        private IEnumerable<InventoryTransactionItem> GetTransactionItems()
        {
            return _inventoryDao.GetTransactionItems(_applicationState.CurrentWorkPeriod.StartDate);
        }

        private IEnumerable<Order> GetOrdersFromRecipes(WorkPeriod workPeriod)
        {
            var startDate = workPeriod.StartDate;
            return _inventoryDao.GetOrdersFromRecipes(startDate);
        }

        private IEnumerable<SalesData> GetSales(WorkPeriod workPeriod)
        {
            var orders = GetOrdersFromRecipes(workPeriod).ToList();
            var salesData = orders.GroupBy(x => new { x.MenuItemName, x.MenuItemId, x.PortionName })
                    .Select(x => new SalesData { MenuItemName = x.Key.MenuItemName, MenuItemId = x.Key.MenuItemId, PortionName = x.Key.PortionName, Total = x.Sum(y => y.Quantity) }).ToList();

            var orderTagValues = orders.SelectMany(x => x.GetOrderTagValues(), (order, ot) => new { OrderTagValues = ot, order.Quantity })
                    .Where(x => x.OrderTagValues.MenuItemId > 0)
                    .GroupBy(x => new { x.OrderTagValues.MenuItemId, x.OrderTagValues.PortionName });

            foreach (var orderTagValue in orderTagValues)
            {
                var tip = orderTagValue;
                var mi = _cacheService.GetMenuItem(x => x.Id == tip.Key.MenuItemId);
                var port = mi.Portions.FirstOrDefault(x => x.Name == tip.Key.PortionName) ?? mi.Portions[0];
                var sd = salesData.SingleOrDefault(x => x.MenuItemId == mi.Id && x.MenuItemName == mi.Name && x.PortionName == port.Name) ?? new SalesData();
                sd.MenuItemId = mi.Id;
                sd.MenuItemName = mi.Name;
                sd.PortionName = port.Name;
                sd.Total += tip.Sum(x => x.OrderTagValues.Quantity * x.Quantity);
                if (!salesData.Contains(sd))
                    salesData.Add(sd);
            }

            return salesData;
        }

        private void UpdateConsumption(PeriodicConsumption pc)
        {
            var sales = GetSales(_applicationState.CurrentWorkPeriod);
            foreach (var sale in sales)
            {
                var portionName = sale.PortionName;
                var menuItemId = sale.MenuItemId;
                var recipe = _inventoryDao.GetRecipe(portionName, menuItemId);
                pc.UpdateConsumption(recipe, sale.MenuItemName, sale.Total);
            }
        }

        private PeriodicConsumption CreateNewPeriodicConsumption()
        {
            var previousPc = GetPreviousPeriodicConsumption();
            var transactionItems = GetTransactionItems().ToList();
            var inventoryItems = _inventoryDao.GetInventoryItems();

            var pc = PeriodicConsumption.Create(_applicationState.CurrentWorkPeriod);
            pc.CreatePeriodicConsumptionItems(inventoryItems, previousPc, transactionItems);
            UpdateConsumption(pc);
            CalculateCost(pc, _applicationState.CurrentWorkPeriod);
            return pc;
        }

        public PeriodicConsumption GetPreviousPeriodicConsumption()
        {
            return _applicationState.PreviousWorkPeriod == null
                       ? null
                       : _inventoryDao.GetPeriodicConsumptionByWorkPeriodId(_applicationState.PreviousWorkPeriod.Id);

        }

        public PeriodicConsumption GetCurrentPeriodicConsumption()
        {
            var pc = _inventoryDao.GetPeriodicConsumptionByWorkPeriodId(_applicationState.CurrentWorkPeriod.Id)
                ?? CreateNewPeriodicConsumption();
            return pc;
        }

        public void CalculateCost(PeriodicConsumption pc, WorkPeriod workPeriod)
        {
            var recipes = GetSales(workPeriod).Select(sale => _inventoryDao.GetRecipe(sale.PortionName, sale.MenuItemId));
            pc.UpdateCost(recipes);
        }

        public IEnumerable<string> GetInventoryItemNames()
        {
            return _inventoryDao.GetInventoryItemNames();
        }

        public IEnumerable<string> GetGroupCodes()
        {
            return _inventoryDao.GetGroupCodes();
        }

        public void SavePeriodicConsumption(PeriodicConsumption pc)
        {
            Dao.Save(pc);
        }

        private void OnWorkperiodStatusChanged(EventParameters<WorkPeriod> obj)
        {
            if (obj.Topic != EventTopicNames.WorkPeriodStatusChanged) return;
            if (!_inventoryDao.RecipeExists()) return;
            if (_applicationState.IsCurrentWorkPeriodOpen)
            {
                if (_applicationState.PreviousWorkPeriod == null) return;
                var pc = GetPreviousPeriodicConsumption();
                if (pc == null) return;
                CalculateCost(pc, _applicationState.PreviousWorkPeriod);
                SavePeriodicConsumption(pc);
            }
            else
            {
                var pc = GetCurrentPeriodicConsumption();
                SavePeriodicConsumption(pc);
            }
        }
    }
}
