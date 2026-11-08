"""Every car the build knows, by key. Add a car type as a module with a Car subclass, then list its variants here."""
from .boxcar import Boxcar
from .wellcar import WellCar

CARS = {car.key: car for car in (
    Boxcar(),
    WellCar(),
)}
