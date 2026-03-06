// import { EventStatus } from "api-client";

// /**
//  * This function is used to get the Integer value of an Enum corresponding to the enum value returned by backend.
//  * @param enumString this is a value of the Enum object.
//  * @param enumObject this is the Enum object where the value enumString comes from.
//  * @returns the enum as an number - corresponding to the enum value returned by backend.
//  */
// export function getEnumNumberByName(enumString: EventStatus, enumObject: object): number {
//     const values = Object.values(enumObject);
//     return values.indexOf(enumString);
// }

// /**
//  * This function is used to compare Enums between the frontend and backend.
//  * @param frontendEnumString this is a value of the Enum object.
//  * @param backendEnumNumber this is a number representation of the Enum returned from the backend.
//  * @param enumObject this is the Enum object where the value frontendEnumString comes from.
//  * @returns a boolean
//  */
// export function compareEnumBetweenFrontendAndBackend(frontendEnumString: EventStatus, backendEnumNumber: EventStatus, enumObject: object): boolean {
//     const frontendEnumNumber = getEnumNumberByName(frontendEnumString, enumObject);
//     return frontendEnumNumber === +backendEnumNumber;
// }
