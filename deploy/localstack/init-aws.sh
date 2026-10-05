#!/bin/bash
# Runs inside LocalStack when it is ready. Mirrors the messaging topology that the AWS CDK
# stack creates in the cloud: one SNS topic, one SQS queue + DLQ per service, and filtered
# subscriptions so each service only receives the message types it handles.
set -euo pipefail

REGION=us-east-1
ACCOUNT=000000000000
TOPIC_ARN=$(awslocal sns create-topic --name streamcart-events --region "$REGION" --query TopicArn --output text)

create_queue() {
  local service=$1
  local filter=$2

  local dlq_url
  dlq_url=$(awslocal sqs create-queue --queue-name "streamcart-${service}-dlq" --region "$REGION" --query QueueUrl --output text)
  local dlq_arn="arn:aws:sqs:${REGION}:${ACCOUNT}:streamcart-${service}-dlq"

  local queue_url
  queue_url=$(awslocal sqs create-queue --queue-name "streamcart-${service}" --region "$REGION" \
    --attributes "{\"VisibilityTimeout\":\"60\",\"RedrivePolicy\":\"{\\\"deadLetterTargetArn\\\":\\\"${dlq_arn}\\\",\\\"maxReceiveCount\\\":\\\"5\\\"}\"}" \
    --query QueueUrl --output text)
  local queue_arn="arn:aws:sqs:${REGION}:${ACCOUNT}:streamcart-${service}"

  local sub_arn
  sub_arn=$(awslocal sns subscribe --topic-arn "$TOPIC_ARN" --protocol sqs --notification-endpoint "$queue_arn" \
    --region "$REGION" --query SubscriptionArn --output text)
  awslocal sns set-subscription-attributes --subscription-arn "$sub_arn" --attribute-name RawMessageDelivery --attribute-value true --region "$REGION"
  awslocal sns set-subscription-attributes --subscription-arn "$sub_arn" --attribute-name FilterPolicy --attribute-value "$filter" --region "$REGION"

  echo "  ${service}: ${queue_url} (dlq ${dlq_url})"
}

echo "Creating StreamCart messaging topology on ${TOPIC_ARN}"
create_queue orders    '{"type":["InventoryReserved","InventoryRejected","InventoryReleased","PaymentSucceeded","PaymentFailed","PaymentRefunded"]}'
create_queue inventory '{"type":["ReserveInventory","ReleaseInventory"]}'
create_queue payments  '{"type":["ProcessPayment","RefundPayment"]}'
echo "StreamCart topology ready."
