##############################################
# Create Service Bus Namespace
##############################################
function Create-ServiceBusNamespace {

  param ([string] $name)

  az servicebus namespace create `
  --name $name `
  --sku Standard `
  > $null

#  az servicebus namespace create `
#  --name $name `
#  --sku Premium `
#  --capacity 2 `
#  > $null

  Write-Host "Service Bus Namespace created"
  Write-Host "  Namespace: $name"
  Write-Host ""
}

##############################################
# Create Service Bus topic
##############################################
function Create-ServiceBusTopic {

  param ([string] $serviceBusNamespace, [string] $topic, [bool] $enableDuplicateDetection = $False)

  az servicebus topic create `
  --namespace-name $serviceBusNamespace `
  --name $topic `
  --enable-duplicate-detection $enableDuplicateDetection  `
  --max-size 5120 `
  > $null

  Write-Host "Service Bus Topic created."
  Write-Host "  Topic: $topic"
  Write-Host ""
}

##############################################
# Create Service Bus topics in parallel
##############################################
function Create-ServiceBusTopicsInParallel {

  param ([string] $serviceBusNamespace, [string[]] $topics)

  $topics | ForEach-Object -Parallel {
      az servicebus topic create `
      --namespace-name $using:serviceBusNamespace `
      --max-size 5120 `
      --name $_ `
      > $null

      Write-Host ("Service Bus Topic created: `n Topic: {0}`n" -f $_)
  }
}

##############################################
# Create Service Bus subscriptions
##############################################
function Create-ServiceBusSubscription {

  param ([string] $serviceBusNamespace, [string] $topic, [string] $subscription)

  az servicebus topic subscription create `
  --namespace-name $serviceBusNamespace `
  --topic-name $topic `
  --name $subscription `
  --enable-session $True `
  > $null

  Write-Host "Service Bus Subscription created."
  Write-Host "  Topic: $topic"
  Write-Host "  Subscription: $subscription"
  Write-Host "  Enable session: true"
  Write-Host ""
}

function Create-ServiceBusForwardSubscription {

  param (
    [string] $serviceBusNamespace, 
    [string] $topic, 
    [string] $subscription,
    [string] $forwardTo)

  az servicebus topic subscription create `
  --namespace-name $serviceBusNamespace `
  --topic-name $topic `
  --name $subscription `
  --forward-to $forwardTo `
  > $null

  Write-Host "Service Bus Subscription created."
  Write-Host "  Topic: $topic"
  Write-Host "  Subscription: $subscription"
  Write-Host "  Forward-To: $forwardTo"
  Write-Host ""
}

##############################################
# Create Service Bus subscription rule (filter and/or action)
##############################################
function Create-ServiceBusSubscriptionRule {

  param (
    [string] $serviceBusNamespace, 
    [string] $topic, 
    [string] $subscription,
    [string] $rule,
    [string] $filter = "1=1",
    [string] $action = "")

  if($action -ne "")
  {
      az servicebus topic subscription rule create `
      --namespace-name $serviceBusNamespace `
      --topic-name $topic `
      --subscription-name $subscription `
      --name $rule `
      --filter-sql-expression $filter `
      --action-sql-expression $action `
      > $null
  }
  else
  {
      az servicebus topic subscription rule create `
      --namespace-name $serviceBusNamespace `
      --topic-name $topic `
      --subscription-name $subscription `
      --name $rule `
      --filter-sql-expression $filter `
      > $null
  }
  
  Write-Host "Service Bus Subscription Rule created."
  Write-Host "  Topic: $topic"
  Write-Host "  Subscription: $subscription"
  Write-Host "  Rule name: $rule"
  Write-Host "  Filter: $filter"
  if($action -ne "")
  {
    Write-Host "  Action: $action"
  }
  Write-Host "  Rule name: $rule"
  Write-Host ""
}

##############################################
# Assign RBAC to topics/subscriptions
##############################################
$SERVICE_BUS_READER_ROLE = "4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0"
$SERVICE_BUS_SENDER_ROLE = "69a216fc-b8fb-44d8-bc22-1f3c2cd27a39"

function Assign-ServiceBusSubscriptionReader {

  param ([string] $assigneeId, [string] $serviceBusNamespace, [string] $topic, [string] $subscription, [string] $resourceGroupName)

  $servicebusNamespaceId = (az servicebus namespace show --resource-group $resourceGroupName --name $serviceBusNamespace | ConvertFrom-Json).id
  Write-Host "Service Bus Namespace Id: $servicebusNamespaceId"

  #$assigneeObjectId = (az ad sp show --id $assigneeId | ConvertFrom-Json).objectId
  #Write-Host "Assignee ObjectIdId: $assigneeObjectId"
  
  az role assignment create --role $SERVICE_BUS_READER_ROLE --assignee $assigneeId --scope $servicebusNamespaceId/topics/$topic
  
  Write-Host "Service Bus role assigned."
  Write-Host "  Scope: topics/$topic/subscriptions/$subscription"
  Write-Host "  Assignee Id: $assigneeId"
  Write-Host "  Role: Reader ($SERVICE_BUS_READER_ROLE)"
  Write-Host ""
}

function Assign-ServiceBusTopicSender {

  param ([string] $assigneeId, [string] $serviceBusNamespace, [string] $topic, [string] $resourceGroupName)

  $servicebusNamespaceId = (az servicebus namespace show --resource-group $resourceGroupName --name $serviceBusNamespace | ConvertFrom-Json).id
  Write-Host "Service Bus Namespace Id: $servicebusNamespaceId"

  #$assigneeObjectId = (az ad sp show --id $assigneeId | ConvertFrom-Json).objectId
  #Write-Host "Assignee ObjectIdId: $assigneeObjectId"

  az role assignment create --role $SERVICE_BUS_SENDER_ROLE --assignee $assigneeId --scope $servicebusNamespaceId/topics/$topic
  
  Write-Host "Service Bus Topic role assigned."
  Write-Host "  Scope: topics/$topic"
  Write-Host "  Assignee Id: $assigneeId"
  Write-Host "  Role: Sender ($SERVICE_BUS_SENDER_ROLE)"
  Write-Host ""
}

function Get-ServiceBusTopicReaderSenderConnectionString {

  param ([string] $serviceBusNamespace, [string] $topic)

   # Create auth rule (send/receive)
    az servicebus topic authorization-rule create `
    --namespace-name $serviceBusNamespace `
    --topic-name $topic `
    --name $topic `
    --rights Send Listen `
    > $null

    # Get connection string
    $result = (az servicebus topic authorization-rule keys list `
    --namespace-name $serviceBusNamespace `
    --topic-name $topic `
    --name $topic `
    | ConvertFrom-Json).primaryConnectionString

    # Remove "EntityPath=..." from connection string
    $entityPathIndex = $result.IndexOf("EntityPath")
    if($entityPathIndex -gt 0)
    {
        $result = $result.Substring(0, $entityPathIndex)
    }

    $result
}

function Get-ServiceBusPrimaryConnectionString {

    param ([string] $serviceBusNamespace, [string] $resourceGroup)

    #Get connection string
    $result = (az servicebus namespace authorization-rule keys list `
    --resource-group $resourceGroup `
    --namespace-name $serviceBusNamespace `
    --name RootManageSharedAccessKey `
    --query primaryConnectionString `
    --output tsv)

    $result
}

##############################################
# Create Service Bus subscriptions in parallel
##############################################
function Create-ServiceBusSubscriptionsInParallel {

  param ([string] $sbNamespace, [string] $nameOfTo, [string] $nameOfFrom, 
            [string] $nameOfEventId, [string] $resolverId, [string] $brokerId, 
            [string] $continuationId, [string] $retryId, [PSObject[]] $endpoints)

# Scope the functions for use inside the parallel loop
    $csbsDef = ${function:Create-ServiceBusSubscription}.ToString()
    $csbsrDef = ${function:Create-ServiceBusSubscriptionRule}.ToString()
    $csbfsDef = ${function:Create-ServiceBusForwardSubscription}.ToString()

    $endpoints | ForEach-Object -ThrottleLimit 10 -Parallel {
        $sbNamespace = $using:sbNamespace
        $nameOfTo = $using:nameOfTo
        $nameOfFrom = $using:nameOfFrom
        $nameOfEventId = $using:nameOfEventId
        $resolverId = $using:resolverId        
        $brokerId = $using:brokerId
        $continuationId = $using:continuationId
        $retryId = $using:retryId
        $endpointId = $_.id
    
        # Ref the functions from outside the loop
        ${function:Create-ServiceBusSubscription} = $using:csbsDef
        ${function:Create-ServiceBusSubscriptionRule} = $using:csbsrDef
        ${function:Create-ServiceBusForwardSubscription} = $using:csbfsDef
    
        # Main subscription
        Create-ServiceBusSubscription     -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $endpointId
        Create-ServiceBusSubscriptionRule -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $endpointId -rule "to-$endpointId" -filter "user.$nameOfTo = '$endpointId'"
    
        # Forward-to-resolver subscription
        Create-ServiceBusForwardSubscription -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $resolverId -forwardTo $resolverId
        Create-ServiceBusSubscriptionRule    -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $resolverId -rule "from-$endpointId" -filter "user.$nameOfTo = '$resolverId'" -action "SET user.$nameOfFrom = '$endpointId'"
        Create-ServiceBusSubscriptionRule    -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $resolverId -rule "to-$endpointId" -filter "user.$nameOfTo = '$endpointId'"

        # Forward-to-broker subscription    
        Create-ServiceBusForwardSubscription -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $brokerId -forwardTo $brokerId
        Create-ServiceBusSubscriptionRule    -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $brokerId -rule "from-$endpointId" -filter "user.$nameOfTo = '$brokerId'" -action "SET user.$nameOfFrom = '$endpointId'; SET user.$nameOfEventId = newid()"
    
        # Forward-from-eventtype-to-endpoint subscriptions
        foreach ($eventType in $_.EventTypesProduced){

            # Get consuming endpoints    
            $consumingEndpoints = New-Object System.Collections.ArrayList
            $eventTypeId = $eventType.Id

            foreach ($endpoint in $endpoints) {
                # Find endpoints that consumes eventtype
                if ( $endpoint.EventTypesConsumed -contains $eventType )
                {
                    $consumingEndpoints.Add($endpoint.Id)
                }                
            }

            # Create forwardsubscription and rule for each subscribing endpoint
            foreach ($ce in $consumingEndpoints) {
                Create-ServiceBusForwardSubscription -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $ce -forwardTo $ce
                Create-ServiceBusSubscriptionRule    -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $ce -rule $eventTypeId -filter "user.EventTypeId = '$eventTypeId'" -action "SET user.$nameOfFrom = '$endpointId'; SET user.EventId = newid(); SET user.To = '$ce';"
            }
        }
    
        # "Forward"-to-self subscription (continuation requests)
        Create-ServiceBusForwardSubscription -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $continuationId -forwardTo $endpointId
        Create-ServiceBusSubscriptionRule    -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $continuationId -rule "continuation" -filter "user.$nameOfTo = '$continuationId'" -action "SET user.$nameOfTo = '$endpointId'; SET user.$nameOfFrom = '$continuationId'"
    
        # "Forward"-to-self subscription (retry requests)
        Create-ServiceBusForwardSubscription -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $retryId -forwardTo $endpointId
        Create-ServiceBusSubscriptionRule    -serviceBusNamespace $sbNamespace -topic $endpointId -subscription $retryId -rule "retry" -filter "user.$nameOfTo = '$retryId'" -action "SET user.$nameOfTo = '$endpointId'; SET user.$nameOfFrom = '$retryId'"
    }
}