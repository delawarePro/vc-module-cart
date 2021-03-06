﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VirtoCommerce.Domain.Cart.Model;
using VirtoCommerce.Domain.Cart.Services;
using VirtoCommerce.Domain.Customer.Model;
using VirtoCommerce.Domain.Customer.Services;
using VirtoCommerce.Domain.Shipping.Model;
using VirtoCommerce.Domain.Store.Model;
using VirtoCommerce.Domain.Store.Services;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.CartModule.Data.Services
{
    public class ShoppingCartBuilderImpl : IShoppingCartBuilder
    {
        private readonly IStoreService _storeService;
        private readonly IShoppingCartService _shoppingCartService;
        private readonly IShoppingCartSearchService _shoppingCartSearchService;
        private readonly IMemberService _memberService;

        private Store _store;

        [CLSCompliant(false)]
        public ShoppingCartBuilderImpl(IStoreService storeService, IShoppingCartService shoppingShoppingCartService, IShoppingCartSearchService shoppingCartSearchService, IMemberService memberService)
        {
            _storeService = storeService;
            _shoppingCartService = shoppingShoppingCartService;
            _shoppingCartSearchService = shoppingCartSearchService;
            _memberService = memberService;
        }

        #region ICartBuilder Members

        public virtual IShoppingCartBuilder TakeCart(ShoppingCart cart)
        {
            if (cart == null)
                throw new ArgumentNullException(nameof(cart));

            Cart = cart;
            return this;
        }

        public virtual IShoppingCartBuilder GetOrCreateCart(string storeId, string customerId, string cartName, string currency, string cultureName)
        {
            var criteria = new ShoppingCartSearchCriteria
            {
                CustomerId = customerId,
                StoreId = storeId,
                Name = cartName,
                Currency = currency
            };

            var searchResult = _shoppingCartSearchService.Search(criteria);
            Cart = searchResult.Results.FirstOrDefault();

            if (Cart == null)
            {
                var customerContact = _memberService.GetByIds(new[] { customerId }).OfType<Contact>().FirstOrDefault();
                Cart = AbstractTypeFactory<ShoppingCart>.TryCreateInstance();
                Cart.Name = cartName;
                Cart.LanguageCode = cultureName;
                Cart.Currency = currency;
                Cart.CustomerId = customerId;
                Cart.CustomerName = customerContact != null ? customerContact.FullName : "Anonymous";
                Cart.IsAnonymous = customerContact == null;
                Cart.StoreId = storeId;

                _shoppingCartService.SaveChanges(new[] { Cart });

                Cart = _shoppingCartService.GetByIds(new[] { Cart.Id }).FirstOrDefault();
            }

            return this;
        }

        public virtual IShoppingCartBuilder AddItem(LineItem lineItem)
        {
            AddLineItem(lineItem);
            return this;
        }

        public virtual IShoppingCartBuilder ChangeItemQuantity(string lineItemId, int quantity)
        {
            var lineItem = Cart.Items.FirstOrDefault(i => i.Id == lineItemId);
            if (lineItem != null)
            {
                InnerChangeItemQuantity(lineItem, quantity);
            }

            return this;
        }

        public virtual IShoppingCartBuilder RemoveItem(string lineItemId)
        {
            var lineItem = Cart.Items.FirstOrDefault(i => i.Id == lineItemId);
            if (lineItem != null)
            {
                Cart.Items.Remove(lineItem);
            }

            return this;
        }

        public virtual IShoppingCartBuilder Clear()
        {
            Cart.Items.Clear();
            return this;
        }

        public virtual IShoppingCartBuilder AddCoupon(string couponCode)
        {
            Cart.Coupon = couponCode;
            return this;
        }

        public virtual IShoppingCartBuilder RemoveCoupon()
        {
            Cart.Coupon = null;
            return this;
        }

        public virtual IShoppingCartBuilder AddOrUpdateShipment(Shipment shipment)
        {
            Shipment existingShipment = null;

            if (!shipment.IsTransient())
            {
                existingShipment = Cart.Shipments.FirstOrDefault(s => s.Id == shipment.Id);
            }

            if (existingShipment != null)
            {
                Cart.Shipments.Remove(existingShipment);
            }

            shipment.Currency = Cart.Currency;
            Cart.Shipments.Add(shipment);

            if (!string.IsNullOrEmpty(shipment.ShipmentMethodCode))
            {
                var availableShippingRates = GetAvailableShippingRates();
                var shippingRate = availableShippingRates.FirstOrDefault(sm => shipment.ShipmentMethodCode.EqualsInvariant(sm.ShippingMethod.Code) && shipment.ShipmentMethodOption.EqualsInvariant(sm.OptionName));
                if (shippingRate == null)
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Unknown shipment method: {0} with option: {1}", shipment.ShipmentMethodCode, shipment.ShipmentMethodOption));
                }
                shipment.ShipmentMethodCode = shippingRate.ShippingMethod.Code;
                shipment.ShipmentMethodOption = shippingRate.OptionName;
                shipment.Price = shippingRate.Rate;
                shipment.DiscountAmount = shippingRate.DiscountAmount;
                shipment.TaxType = shippingRate.ShippingMethod.TaxType;
            }

            return this;
        }

        public virtual IShoppingCartBuilder RemoveShipment(string shipmentId)
        {
            var shipment = Cart.Shipments.FirstOrDefault(s => s.Id == shipmentId);
            if (shipment != null)
            {
                Cart.Shipments.Remove(shipment);
            }

            return this;
        }

        public virtual IShoppingCartBuilder AddOrUpdatePayment(Payment payment)
        {
            Payment existingPayment = null;

            if (!payment.IsTransient())
            {
                existingPayment = Cart.Payments.FirstOrDefault(s => s.Id == payment.Id);
            }

            if (existingPayment != null)
            {
                Cart.Payments.Remove(existingPayment);
            }
            Cart.Payments.Add(payment);

            if (!string.IsNullOrEmpty(payment.PaymentGatewayCode))
            {
                var availablePaymentMethods = GetAvailablePaymentMethods();
                var paymentMethod = availablePaymentMethods.FirstOrDefault(pm => string.Equals(pm.Code, payment.PaymentGatewayCode, StringComparison.InvariantCultureIgnoreCase));
                if (paymentMethod == null)
                {
                    throw new InvalidOperationException("Unknown payment method " + payment.PaymentGatewayCode);
                }
            }

            return this;
        }

        public virtual IShoppingCartBuilder MergeWithCart(ShoppingCart cart)
        {
            foreach (var lineItem in cart.Items)
            {
                AddLineItem(lineItem);
            }

            Cart.Coupon = cart.Coupon;

            Cart.Shipments.Clear();
            Cart.Shipments = cart.Shipments;

            Cart.Payments.Clear();
            Cart.Payments = cart.Payments;

            _shoppingCartService.Delete(new[] { cart.Id });

            return this;
        }

        public virtual IShoppingCartBuilder RemoveCart()
        {
            _shoppingCartService.Delete(new[] { Cart.Id });
            return this;
        }

        public virtual ICollection<ShippingRate> GetAvailableShippingRates()
        {
            // TODO: Remake with shipmentId
            var shippingEvaluationContext = new ShippingEvaluationContext(Cart);

            var activeAvailableShippingMethods = Store.ShippingMethods.Where(x => x.IsActive).ToList();

            var availableShippingRates = activeAvailableShippingMethods
                .SelectMany(x => x.CalculateRates(shippingEvaluationContext))
                .Where(x => x.ShippingMethod == null || x.ShippingMethod.IsActive)
                .ToArray();

            return availableShippingRates;
        }

        public virtual ICollection<Domain.Payment.Model.PaymentMethod> GetAvailablePaymentMethods()
        {
            return Store.PaymentMethods.Where(x => x.IsActive).ToList();
        }

        public virtual void Save()
        {
            _shoppingCartService.SaveChanges(new[] { Cart });
        }

        public ShoppingCart Cart { get; private set; }

        #endregion


        protected Store Store => _store ?? (_store = _storeService.GetById(Cart.StoreId));


        protected virtual void InnerChangeItemQuantity(LineItem lineItem, int quantity)
        {
            if (lineItem != null)
            {
                if (quantity > 0)
                {
                    lineItem.Quantity = quantity;
                }
                else
                {
                    Cart.Items.Remove(lineItem);
                }
            }
        }

        protected virtual void AddLineItem(LineItem lineItem)
        {
            var existingLineItem = Cart.Items.FirstOrDefault(li => li.ProductId == lineItem.ProductId);
            if (existingLineItem != null)
            {
                existingLineItem.Quantity += lineItem.Quantity;
            }
            else
            {
                lineItem.Id = null;
                Cart.Items.Add(lineItem);
            }
        }
    }
}
