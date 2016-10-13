﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.EventHubs.Amqp
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Amqp;
    using Microsoft.Azure.Amqp.Framing;

    class AmqpEventDataSender : EventDataSender
    {
        int deliveryCount;
        readonly ActiveClientLinkManager clientLinkManager;

        internal AmqpEventDataSender(AmqpEventHubClient eventHubClient, string partitionId)
            : base(eventHubClient, partitionId)
        {
            if (!string.IsNullOrEmpty(partitionId))
            {
                this.Path = $"{eventHubClient.EventHubName}/Partitions/{partitionId}";
            }
            else
            {
                this.Path = eventHubClient.EventHubName;
            }

            this.SendLinkManager = new FaultTolerantAmqpObject<SendingAmqpLink>(this.CreateLinkAsync, this.CloseSession);
            this.clientLinkManager = new ActiveClientLinkManager((AmqpEventHubClient)this.EventHubClient);
        }

        string Path { get; }

        FaultTolerantAmqpObject<SendingAmqpLink> SendLinkManager { get; }

        public override Task CloseAsync()
        {
            this.clientLinkManager.Close();
            return this.SendLinkManager.CloseAsync();
        }

        protected override async Task OnSendAsync(IEnumerable<EventData> eventDatas, string partitionKey)
        {
            bool shouldRetry;

            var timeoutHelper = new TimeoutHelper(this.EventHubClient.ConnectionStringBuilder.OperationTimeout, true);

            do
            {
                using (AmqpMessage amqpMessage = AmqpMessageConverter.EventDatasToAmqpMessage(eventDatas, partitionKey, true))
                {
                    shouldRetry = false;

                    try
                    {
                        try
                        {
                            var amqpLink = await this.SendLinkManager.GetOrCreateAsync(timeoutHelper.RemainingTime());
                            if (amqpLink.Settings.MaxMessageSize.HasValue)
                            {
                                ulong size = (ulong)amqpMessage.SerializedMessageSize;
                                if (size > amqpLink.Settings.MaxMessageSize.Value)
                                {
                                    throw new MessageSizeExceededException(amqpMessage.DeliveryId.Value, size, amqpLink.Settings.MaxMessageSize.Value);
                                }
                            }

                            Outcome outcome = await amqpLink.SendMessageAsync(amqpMessage, this.GetNextDeliveryTag(), AmqpConstants.NullBinary, timeoutHelper.RemainingTime());
                            if (outcome.DescriptorCode != Accepted.Code)
                            {
                                Rejected rejected = (Rejected)outcome;
                                throw new AmqpException(rejected.Error);
                            }

                            this.EventHubClient.RetryPolicy.ResetRetryCount(this.ClientId);
                        }
                        catch (AmqpException amqpException)
                        {
                            throw AmqpExceptionHelper.ToMessagingContract(amqpException.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Evaluate retry condition?
                        this.EventHubClient.RetryPolicy.IncrementRetryCount(this.ClientId);
                        TimeSpan? retryInterval = this.EventHubClient.RetryPolicy.GetNextRetryInterval(this.ClientId, ex, timeoutHelper.RemainingTime());
                        if (retryInterval != null)
                        {
                            await Task.Delay(retryInterval.Value);
                            shouldRetry = true;
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
            } while (shouldRetry);
        }

        ArraySegment<byte> GetNextDeliveryTag()
        {
            int deliveryId = Interlocked.Increment(ref this.deliveryCount);
            return new ArraySegment<byte>(BitConverter.GetBytes(deliveryId));
        }

        async Task<SendingAmqpLink> CreateLinkAsync(TimeSpan timeout)
        {
            var amqpEventHubClient = ((AmqpEventHubClient)this.EventHubClient);
            var csb = amqpEventHubClient.ConnectionStringBuilder;
            var timeoutHelper = new TimeoutHelper(csb.OperationTimeout);
            AmqpConnection connection = await amqpEventHubClient.ConnectionManager.GetOrCreateAsync(timeoutHelper.RemainingTime());

            // Authenticate over CBS
            var cbsLink = connection.Extensions.Find<AmqpCbsLink>();

            ICbsTokenProvider cbsTokenProvider = amqpEventHubClient.CbsTokenProvider;
            Uri address = new Uri(csb.Endpoint, this.Path);
            string audience = address.AbsoluteUri;
            string resource = address.AbsoluteUri;
            var expiresAt = await cbsLink.SendTokenAsync(cbsTokenProvider, address, audience, resource, new[] { ClaimConstants.Send }, timeoutHelper.RemainingTime());

            AmqpSession session = null;
            try
            {
                // Create our Session
                var sessionSettings = new AmqpSessionSettings { Properties = new Fields() };
                session = connection.CreateSession(sessionSettings);
                await session.OpenAsync(timeoutHelper.RemainingTime());

                // Create our Link
                var linkSettings = new AmqpLinkSettings();
                linkSettings.AddProperty(AmqpClientConstants.TimeoutName, (uint)timeoutHelper.RemainingTime().TotalMilliseconds);
                linkSettings.AddProperty(AmqpClientConstants.EntityTypeName, (int)MessagingEntityType.EventHub);
                linkSettings.Role = false;
                linkSettings.InitialDeliveryCount = 0;
                linkSettings.Target = new Target { Address = address.AbsolutePath };
                linkSettings.Source = new Source { Address = this.ClientId };

                var link = new SendingAmqpLink(linkSettings);
                linkSettings.LinkName = $"{amqpEventHubClient.ContainerId};{connection.Identifier}:{session.Identifier}:{link.Identifier}";
                link.AttachTo(session);

                await link.OpenAsync(timeoutHelper.RemainingTime());

                var activeClientLink = new ActiveClientLink(
                    link,
                    this.EventHubClient.ConnectionStringBuilder.Endpoint.AbsoluteUri, // audience
                    this.EventHubClient.ConnectionStringBuilder.Endpoint.AbsoluteUri, // endpointUri
                    new[] { ClaimConstants.Send },
                    true,
                    expiresAt);

                this.clientLinkManager.SetActiveLink(activeClientLink);

                return link;
            }
            catch
            {
                // Cleanup any session (and thus link) in case of exception.
                session?.Abort();
                throw;
            }
        }

        void CloseSession(SendingAmqpLink link)
        {
            // Note we close the session (which includes the link).
            link.Session.SafeClose();
        }
    }
}
