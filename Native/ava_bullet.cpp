#include <btBulletDynamicsCommon.h>
#include <BulletDynamics/ConstraintSolver/btGeneric6DofSpring2Constraint.h>
#include <cmath>
#include <memory>
#include <unordered_set>
#include <vector>

#define API extern "C" __declspec(dllexport)

struct Vec { float x, y, z; };
struct Pose { Vec position; float x, y, z, w; };
struct BodyDesc {
    Pose pose;
    Vec size; // sphere radius; box half extents; capsule radius and cylindrical height
    float mass, linearDamping, angularDamping, friction, restitution, gravityScale;
    int shape, group, mask, restricted;
};
static btVector3 vector(Vec v) { return btVector3(v.x,v.y,v.z); }
static btTransform transform(Pose p) {
    return btTransform(btQuaternion(p.x,p.y,p.z,p.w).normalized(),vector(p.position));
}
static bool finite(Vec v) { return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z); }
static bool unit(float v) { return v>=0 && v<=1; }
static bool validBody(const BodyDesc& d) {
    if(!finite(d.size) || !std::isfinite(d.mass) || d.mass<0 || !unit(d.restitution) ||
       !unit(d.linearDamping) || !unit(d.angularDamping) || !std::isfinite(d.friction) || d.friction<0 ||
       !std::isfinite(d.gravityScale) || d.group<0 || d.group>65535 || d.mask<0 || d.mask>65535)return false;
    switch(d.shape) {
        case 0:return d.size.x>0;
        case 1:return d.size.x>0 && d.size.y>0 && d.size.z>0;
        case 2:return d.size.x>0 && d.size.y>=0;
        case 3:return d.mass==0;
        default:return false;
    }
}
static bool validPose(Pose p) {
    float norm=p.x*p.x+p.y*p.y+p.z*p.z+p.w*p.w;
    return std::isfinite(norm) && norm>0.0001f && std::isfinite(p.position.x) &&
           std::isfinite(p.position.y) && std::isfinite(p.position.z);
}

struct Body {
    std::unique_ptr<btCollisionShape> shape;
    std::unique_ptr<btDefaultMotionState> motion;
    std::unique_ptr<btRigidBody> rigid;
    float mass, gravityScale;
    bool kinematic, restricted;
    std::unordered_set<int> allowed;
};
struct Dispatcher : btCollisionDispatcher {
    using btCollisionDispatcher::btCollisionDispatcher;
    bool needsCollision(const btCollisionObject* a, const btCollisionObject* b) override {
        const auto* first=static_cast<const Body*>(a->getUserPointer());
        const auto* second=static_cast<const Body*>(b->getUserPointer());
        if(first->restricted && !first->allowed.count(b->getUserIndex()))return false;
        if(second->restricted && !second->allowed.count(a->getUserIndex()))return false;
        return btCollisionDispatcher::needsCollision(a,b);
    }
};
struct World {
    btDefaultCollisionConfiguration config;
    Dispatcher dispatcher{&config};
    btDbvtBroadphase broadphase;
    btSequentialImpulseConstraintSolver solver;
    btDiscreteDynamicsWorld dynamics{&dispatcher,&broadphase,&solver,&config};
    std::vector<std::unique_ptr<Body>> bodies;
    std::vector<std::unique_ptr<btGeneric6DofSpring2Constraint>> joints;
    World() {
        dynamics.setGravity(btVector3(0,0,0));
        dynamics.getSolverInfo().m_numIterations=20;
        dynamics.getSolverInfo().m_solverMode &= ~SOLVER_RANDMIZE_ORDER;
    }
    ~World() {
        for(auto& c:joints)dynamics.removeConstraint(c.get());
        for(auto& b:bodies)dynamics.removeRigidBody(b->rigid.get());
    }
    Body* get(int i) { return i>=0 && static_cast<size_t>(i)<bodies.size()?bodies[i].get():nullptr; }
};

API int ava_bullet_version() { return btGetVersion(); }
API World* ava_bullet_create() { try { return new World(); } catch(...) { return nullptr; } }
API void ava_bullet_destroy(World* w) { delete w; }

API int ava_bullet_body(World* w, const BodyDesc* d) {
    if(!w || !d || !validPose(d->pose) || !validBody(*d))return -1;
    try {
        auto b=std::make_unique<Body>();
        switch(d->shape) {
            case 0:b->shape=std::make_unique<btSphereShape>(d->size.x);break;
            case 1:b->shape=std::make_unique<btBoxShape>(vector(d->size));break;
            case 2:b->shape=std::make_unique<btCapsuleShape>(d->size.x,d->size.y);break;
            case 3:b->shape=std::make_unique<btStaticPlaneShape>(btVector3(0,1,0),btScalar(0));break;
            default:return -1;
        }
        b->mass=d->mass;b->gravityScale=d->gravityScale;b->kinematic=d->mass==0;
        b->restricted=d->restricted!=0;
        b->motion=std::make_unique<btDefaultMotionState>(transform(d->pose));
        btVector3 inertia(0,0,0);
        if(b->mass>0)b->shape->calculateLocalInertia(b->mass,inertia);
        btRigidBody::btRigidBodyConstructionInfo info(b->mass,b->motion.get(),b->shape.get(),inertia);
        info.m_linearDamping=d->linearDamping;info.m_angularDamping=d->angularDamping;
        info.m_friction=d->friction;info.m_restitution=d->restitution;
        b->rigid=std::make_unique<btRigidBody>(info);
        if(b->kinematic)b->rigid->setCollisionFlags((b->rigid->getCollisionFlags() & ~btCollisionObject::CF_STATIC_OBJECT)|btCollisionObject::CF_KINEMATIC_OBJECT);
        b->rigid->setActivationState(DISABLE_DEACTIVATION);
        int id=static_cast<int>(w->bodies.size()); b->rigid->setUserIndex(id);b->rigid->setUserPointer(b.get());
        w->bodies.push_back(std::move(b));
        w->dynamics.addRigidBody(w->get(id)->rigid.get(),d->group,d->mask);
        return id;
    } catch(...) { return -1; }
}

API int ava_bullet_allow(World* w,int collider,int body) {
    if(!w || !w->get(collider) || !w->get(body))return 0;
    try {w->get(collider)->allowed.insert(body);return 1;}catch(...){return 0;}
}

API int ava_bullet_joint(World* w,int parent,int child,const Pose* pivot,const Vec* limits,float spring,float damping) {
    if(!w || parent==child || !w->get(parent) || !w->get(child) || !pivot || !validPose(*pivot) ||
       !limits || !finite(*limits) || !std::isfinite(spring) || spring<0 || !unit(damping))return 0;
    try {
        auto& a=*w->get(parent)->rigid;auto& b=*w->get(child)->rigid;
        const btTransform bind=transform(*pivot);
        auto c=std::make_unique<btGeneric6DofSpring2Constraint>(a,b,a.getWorldTransform().inverse()*bind,b.getWorldTransform().inverse()*bind);
        c->setLinearLowerLimit(btVector3(0,0,0));c->setLinearUpperLimit(btVector3(0,0,0));
        c->setAngularLowerLimit(-vector(*limits));c->setAngularUpperLimit(vector(*limits));
        for(int axis=3;axis<6;axis++)if(spring>0 && vector(*limits)[axis-3]>0) {
            c->enableSpring(axis,true);c->setStiffness(axis,spring);c->setDamping(axis,damping);c->setEquilibriumPoint(axis,0);
        }
        w->joints.push_back(std::move(c));w->dynamics.addConstraint(w->joints.back().get(),true);
        return 1;
    } catch(...) { return 0; }
}

API void ava_bullet_drive(World* w,int id,int follow,const Pose* target) {
    auto* b=w?w->get(id):nullptr;if(!b || !target || !validPose(*target))return;
    bool kinematic=follow!=0 || b->mass==0;
    auto& rb=*b->rigid;
    if(b->kinematic!=kinematic) {
        btVector3 inertia(0,0,0);
        if(!kinematic)b->shape->calculateLocalInertia(b->mass,inertia);
        rb.setMassProps(kinematic?0:b->mass,inertia);
        int flags=rb.getCollisionFlags() & ~(btCollisionObject::CF_STATIC_OBJECT | btCollisionObject::CF_KINEMATIC_OBJECT);
        rb.setCollisionFlags(kinematic?flags|btCollisionObject::CF_KINEMATIC_OBJECT:flags);rb.updateInertiaTensor();
        rb.clearForces();rb.setLinearVelocity(btVector3(0,0,0));rb.setAngularVelocity(btVector3(0,0,0));
        rb.setInterpolationWorldTransform(transform(*target));rb.setWorldTransform(transform(*target));
        b->kinematic=kinematic;rb.activate(true);
    }
    if(kinematic)b->motion->setWorldTransform(transform(*target));
}
API void ava_bullet_step(World* w,const Vec* gravity) {
    if(!w || !gravity || !finite(*gravity))return;
    for(auto& b:w->bodies)if(!b->kinematic)b->rigid->setGravity(vector(*gravity)*b->gravityScale);
    w->dynamics.stepSimulation(btScalar(1)/60,0);
}
API void ava_bullet_read(World* w,int id,Pose* result) {
    auto* b=w?w->get(id):nullptr;if(!b || !result)return;
    const auto& t=b->rigid->getWorldTransform();auto v=t.getOrigin();auto q=t.getRotation();
    *result={{v.x(),v.y(),v.z()},q.x(),q.y(),q.z(),q.w()};
}
