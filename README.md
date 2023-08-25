# MirrorCSPhysics
An implementation of Client-Side Prediction done with Mirror. This project was just something I did for fun.
# Features
- Adds a new variant of Mirror's Base NetworkTransform class that automatically client side prediction on attached components.
- Easy to incorperate into different projects (if you are me and understand what to do)
- Easy to tune and tweak for different properties
- Works with kinematic bodies and rigidbodies with physics (you have to tune this)
# TODO
- Make it easy to use for people who are not me
- Rename scripts to be cooler
- Documentation
- Bugs?
# How to Use
0. Grab everything in the "Keen's Things" folder and move into your project
1. Drag the NetworkCCmd.cs script into your controller of choice
2. Create a reference of the NetworkCCmd object in your MovementController script.
3. Make a hasInput() function that checks if you are currently doing any button presses (including mouse movements).
4. Implement the IController interface inside of your Controller script.
5. Create a InputCmd command with all of your inputs at that tick as well as calling function NetworkCCmd.InputDown(cmd) AFTER your inputs have been executed on your controller.
6. Run NetworkCCmd.InputUp() when you are NOT making any inputs.
7. Tweak smoothing parameters and try it out!

Note the project is like super bare bones and I know the instructions kind of suck, but check out the Controller.cs in Keen's Things for an example of a controller with this implemented.
