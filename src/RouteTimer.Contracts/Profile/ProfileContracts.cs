namespace RouteTimer.Contracts.Profile;

public sealed record UpdateProfileRequest(double RiderWeightKg, double BikeAndEquipmentWeightKg);
public sealed record ProfileResponse(double RiderWeightKg, double BikeAndEquipmentWeightKg);
