##############################################
# Throws exception when lastExitCode was not 0
##############################################
function Throw-WhenError {

  param (
    [string]
    $msg
  )

  if ($LastExitCode -gt 0) {
    Write-Error $msg
    throw
  }
}

##############################################
# Set Resource Group and its location as default
##############################################
function Set-Default-Resource-Group {
    param (
        [string]
        $resourceGroupName
    )
    $resourceGroup = az group show --name $resourceGroupName | ConvertFrom-Json
    Throw-WhenError -msg "Resource Group '$resourceGroupName' not found."

    $location = $resourceGroup.location

    az configure `
    --defaults `
        location=$location `
        group=$resourceGroupName
}

##############################################
# Install required extension
##############################################
function Add-Extension {
    param (
        [string]
        $name
    )
    $WarningPreference = 'SilentlyContinue'
    az extension add --name $name
}

##############################################
# Create Random string
##############################################

Function Get-RandomAlphanumericString {
	
	[CmdletBinding()]
	Param (
        [int] $length = 8
	)

	Begin{
	}

	Process{
        Write-Output ( -join ((0x30..0x39) + ( 0x41..0x5A) + ( 0x61..0x7A) | Get-Random -Count $length  | % {[char]$_}) )
	}	
}