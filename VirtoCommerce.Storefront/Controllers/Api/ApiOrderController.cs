using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.Storefront.AutoRestClients.OrdersModuleApi;
using VirtoCommerce.Storefront.AutoRestClients.StoreModuleApi;
using VirtoCommerce.Storefront.Domain;
using VirtoCommerce.Storefront.Infrastructure;
using VirtoCommerce.Storefront.Infrastructure.Swagger;
using VirtoCommerce.Storefront.Model;
using VirtoCommerce.Storefront.Model.Common;
using VirtoCommerce.Storefront.Model.Common.Exceptions;
using VirtoCommerce.Storefront.Model.Order;
using orderModel = VirtoCommerce.Storefront.AutoRestClients.OrdersModuleApi.Models;

namespace VirtoCommerce.Storefront.Controllers.Api
{
    [StorefrontApiRoute("orders")]
    public class ApiOrderController : StorefrontControllerBase
    {
        private readonly IOrderModule _orderApi;
        private readonly IStoreModule _storeApi;

        public ApiOrderController(IWorkContextAccessor workContextAccessor, IStorefrontUrlBuilder urlBuilder, IOrderModule orderApi, IStoreModule storeApi)
            : base(workContextAccessor, urlBuilder)
        {
            _orderApi = orderApi;
            _storeApi = storeApi;
        }

        // POST: storefrontapi/orders/search
        [HttpPost("search")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult<GenericSearchResult<CustomerOrder>>> SearchCustomerOrders([FromBody] OrderSearchCriteria criteria)
        {
            if (criteria == null)
            {
                criteria = new OrderSearchCriteria();
            }
            //Does not allow to see a other customer orders
            criteria.CustomerId = WorkContext.CurrentUser.Id;

            var result = await _orderApi.SearchAsync(criteria.ToSearchCriteriaDto());

            return new GenericSearchResult<CustomerOrder>
            {
                Results = result.CustomerOrders.Select(x => x.ToCustomerOrder(WorkContext.AllCurrencies, WorkContext.CurrentLanguage)).ToArray(),
                TotalCount = result.TotalCount ?? default(int),
            };
        }

        // GET: storefrontapi/orders/{orderNumber}
        [HttpGet("{orderNumber}")]
        public async Task<ActionResult<CustomerOrder>> GetCustomerOrder(string orderNumber)
        {
            var retVal = await GetOrderByNumber(orderNumber);
            return retVal;
        }

        // GET: storefrontapi/orders/{orderNumber}/newpaymentdata
        [HttpGet("{orderNumber}/newpaymentdata")]
        public async Task<ActionResult<NewPaymentData>> GetNewPaymentData(string orderNumber)
        {
            var order = await GetOrderByNumber(orderNumber);

            var storeDto = await _storeApi.GetStoreByIdAsync(order.StoreId);
            var paymentMethods = storeDto.PaymentMethods
                                        .Where(x => x.IsActive.Value)
                                        .Select(x => x.ToPaymentMethod(order));

            var paymentDto = await _orderApi.GetNewPaymentAsync(order.Id);
            var payment = paymentDto.ToOrderInPayment(WorkContext.AllCurrencies, WorkContext.CurrentLanguage);

            return new NewPaymentData
            {
                Payment = payment,
                PaymentMethods = paymentMethods,
                Order = order
            };
        }

        // POST: storefrontapi/orders/{orderNumber}/payments/{paymentNumber}/cancel
        [HttpPost("{orderNumber}/payments/{paymentNumber}/cancel")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> CancelPayment(string orderNumber, string paymentNumber)
        {
            //Need lock to prevent concurrent access to same object
            using (await AsyncLock.GetLockByKey(GetAsyncLockKey(orderNumber, WorkContext)).LockAsync())
            {
                var orderDto = await GetOrderDtoByNumber(orderNumber);
                var payment = orderDto.InPayments.FirstOrDefault(x => x.Number.EqualsInvariant(paymentNumber));
                if (payment != null)
                {
                    payment.IsCancelled = true;
                    payment.CancelReason = "Canceled by customer";
                    payment.CancelledDate = DateTime.UtcNow;
                    payment.PaymentStatus = "Cancelled";
                    await _orderApi.UpdateAsync(orderDto);
                }
            }
            return Ok();
        }

        // POST: storefrontapi/orders/{orderNumber}/payments/{paymentNumber}/process
        [HttpPost("{orderNumber}/payments/{paymentNumber}/process")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult<ProcessOrderPaymentResult>> ProcessOrderPayment(string orderNumber, string paymentNumber, [FromBody][SwaggerOptional] orderModel.BankCardInfo bankCardInfo)
        {
            //Need lock to prevent concurrent access to same order
            using (await AsyncLock.GetLockByKey(GetAsyncLockKey(orderNumber, WorkContext)).LockAsync())
            {
                var orderDto = await GetOrderDtoByNumber(orderNumber);
                var paymentDto = orderDto.InPayments.FirstOrDefault(x => x.Number.EqualsInvariant(paymentNumber));
                if (paymentDto == null)
                {
                    throw new StorefrontException("payment " + paymentNumber + " not found");
                }
                var processingResult = await _orderApi.ProcessOrderPaymentsAsync(orderDto.Id, paymentDto.Id, bankCardInfo);
                return new ProcessOrderPaymentResult
                {
                    OrderProcessingResult = processingResult,
                    PaymentMethod = paymentDto.PaymentMethod
                };
            }
        }

        // POST: storefrontapi/orders/{orderNumber}/payments
        [HttpPost("{orderNumber}/payments")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult<orderModel.PaymentIn>> AddOrUpdateOrderPayment(string orderNumber, [FromBody] PaymentIn payment)
        {
            if (payment.Sum.Amount == 0)
            {
                throw new StorefrontException("Valid payment amount is required");
            }
            //Need to lock to prevent concurrent access to same object
            using (await AsyncLock.GetLockByKey(GetAsyncLockKey(orderNumber, WorkContext)).LockAsync())
            {
                var orderDto = await GetOrderDtoByNumber(orderNumber);
                var paymentDto = orderDto.InPayments.FirstOrDefault(x => x.Id.EqualsInvariant(payment.Id));
                if (paymentDto == null)
                {
                    paymentDto = payment.ToOrderPaymentInDto();
                    paymentDto.CustomerId = WorkContext.CurrentUser.Id;
                    paymentDto.CustomerName = WorkContext.CurrentUser.UserName;
                    paymentDto.Status = "New";
                    orderDto.InPayments.Add(paymentDto);
                }
                else
                {
                    paymentDto.BillingAddress = payment.BillingAddress != null ? payment.BillingAddress.ToOrderAddressDto() : null;
                }

                await _orderApi.UpdateAsync(orderDto);
                //Need to return payment with generated id
                orderDto = await _orderApi.GetByIdAsync(orderDto.Id);
                if (string.IsNullOrEmpty(paymentDto.Id))
                {
                    //Because we don't know the new payment id we need to get latest payment with same gateway code
                    paymentDto = orderDto.InPayments.Where(x => x.GatewayCode.EqualsInvariant(payment.GatewayCode)).OrderByDescending(x => x.CreatedDate).FirstOrDefault();
                }
                return paymentDto;
            }

        }

        // GET: storefrontapi/orders/{orderNumber}/invoice
        [HttpGet("{orderNumber}/invoice")]
        [SwaggerFileResponse]
        public async Task<ActionResult> GetInvoicePdf(string orderNumber)
        {
            var stream = await _orderApi.GetInvoicePdfAsync(orderNumber);

            return File(stream, "application/pdf");
        }

        private async Task<CustomerOrder> GetOrderByNumber(string number)
        {
            var order = await GetOrderDtoByNumber(number);

            WorkContext.CurrentOrder = order.ToCustomerOrder(WorkContext.AllCurrencies, WorkContext.CurrentLanguage);
            return WorkContext.CurrentOrder;
        }

        private async Task<orderModel.CustomerOrder> GetOrderDtoByNumber(string number)
        {
            var order = await _orderApi.GetByNumberAsync(number);

            if (order == null || order.CustomerId != WorkContext.CurrentUser.Id)
            {
                throw new StorefrontException($"Order with number {{ number }} not found (or not belongs to current user)");
            }

            return order;
        }

        private static string GetAsyncLockKey(string orderNumber, WorkContext ctx)
        {
            return string.Join(":", "Order", orderNumber, ctx.CurrentStore.Id, ctx.CurrentUser.Id);
        }
    }
}
